using System.IO.Compression;
using System.Text;
using OokiGrader.SchoolManager.PassFail;

namespace OokiGrader.SchoolManager.Tests;

public sealed class PassFailWorkbookReaderTests
{
    [Fact]
    public void ReadFindsHeaderAndDropsNameAndUnnamedColumns()
    {
        using var workbook = CreateWorkbook(
            [
                ["小６ 社会暗記テスト合格表"],
                [],
                ["四谷大塚ＩＤ", "生徒ID", "出席番号", "氏名", "クラス", "", "第1回", "第2回"],
                ["10000001", "20000001", "101", "山田太郎", "A", "秘密", "○", "●"],
                ["10000002", "20000002", "102", "佐藤花子", "B", "秘密", "●", "○"],
            ]);

        var table = PassFailWorkbookReader.Read(workbook);

        Assert.Equal("小６ 社会暗記テスト合格表", table.SourceTitle);
        Assert.Equal(["第1回", "第2回"], table.Columns);
        Assert.Equal("10000001", table.Rows[0].StudentNumber);
        Assert.Equal("小6", table.Rows[0].GradeKey);
        Assert.Equal("A", table.Rows[0].ClassLabel);
        Assert.Equal(["○", "●"], table.Rows[0].Values);
        Assert.DoesNotContain("山田太郎", string.Join('|', table.Rows[0].Values));
        Assert.DoesNotContain("20000001", string.Join('|', table.Rows[0].Values));
        Assert.DoesNotContain("101", string.Join('|', table.Rows[0].Values));
        Assert.DoesNotContain("秘密", string.Join('|', table.Rows[0].Values));
        Assert.Contains("20000001", table.SensitiveTokens);
    }

    [Fact]
    public void ReadRejectsWorkbookWithoutRequiredIdentityColumns()
    {
        using var workbook = CreateWorkbook(
            [
                ["合否表"],
                ["氏名", "第1回"],
                ["山田太郎", "○"],
            ]);

        var exception = Assert.Throws<PassFailWorkbookException>(
            () => PassFailWorkbookReader.Read(workbook));

        Assert.Equal("pass_fail_header_missing", exception.Code);
    }

    [Fact]
    public void ReadRejectsFreeformResultValues()
    {
        using var workbook = CreateWorkbook(
            [
                ["小６ 合否表"],
                ["四谷大塚ID", "クラス", "第1回"],
                ["10000001", "A", "保護者向けメモ"],
            ]);

        var exception = Assert.Throws<PassFailWorkbookException>(
            () => PassFailWorkbookReader.Read(workbook));

        Assert.Equal("pass_fail_value_unsafe", exception.Code);
    }

    private static MemoryStream CreateWorkbook(IReadOnlyList<string[]> rows)
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Write(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            Write(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Write(archive, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="合否表" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Write(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            var sheet = new StringBuilder("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                """);
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                sheet.Append("<row r=\"").Append(rowIndex + 1).Append("\">");
                for (var columnIndex = 0; columnIndex < rows[rowIndex].Length; columnIndex++)
                {
                    var value = System.Security.SecurityElement.Escape(
                        rows[rowIndex][columnIndex]);
                    sheet.Append("<c r=\"")
                        .Append(ColumnName(columnIndex)).Append(rowIndex + 1)
                        .Append("\" t=\"inlineStr\"><is><t>")
                        .Append(value)
                        .Append("</t></is></c>");
                }

                sheet.Append("</row>");
            }

            sheet.Append("</sheetData></worksheet>");
            Write(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        output.Position = 0;
        return output;
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string ColumnName(int index)
    {
        var value = index + 1;
        var name = string.Empty;
        while (value > 0)
        {
            value--;
            name = (char)('A' + (value % 26)) + name;
            value /= 26;
        }

        return name;
    }
}
