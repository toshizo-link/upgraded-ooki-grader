from pathlib import Path
from collections import Counter
from html import escape
import json, re, zipfile, hashlib, io, sys, os
import xml.etree.ElementTree as ET
from PIL import Image as PILImage
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4, landscape
from reportlab.lib.styles import ParagraphStyle
from reportlab.platypus import Paragraph, Table, TableStyle
from pypdf import PdfReader

sys.stdout.reconfigure(encoding='utf-8')
ROOT=Path(__file__).resolve().parents[2]
E=Path(os.environ.get('OOKI_WORKFLOW_EVIDENCE', r'C:\Users\SEACL\Documents\OokiGrader-Workflow-20260919'))
OUT=ROOT/'output/pdf/OokiGrader-Real-Life-Workflow-and-Test-Guide-2026-09-19.pdf'
DATA=ROOT/'output/pdf/workflow_guide_content.json'
for name, fn in [('A','arial.ttf'),('AB','arialbd.ttf'),('AI','ariali.ttf'),('JP','meiryo.ttc')]:
    pdfmetrics.registerFont(TTFont(name,'C:/Windows/Fonts/'+fn))
pdfmetrics.registerFontFamily('A',normal='A',bold='AB',italic='AI',boldItalic='AB')
W,H=landscape(A4); M=38; CW=W-2*M
NAVY=colors.HexColor('#143344'); TEAL=colors.HexColor('#067F83'); INK=colors.HexColor('#223645')
GRAY=colors.HexColor('#526879'); LINE=colors.HexColor('#D4E0E6'); PALE=colors.HexColor('#EDF5F6')
AMBER=colors.HexColor('#9A5E06'); RED=colors.HexColor('#AD3340'); WHITE=colors.white
styles={
 'body':ParagraphStyle('body',fontName='A',fontSize=10.6,leading=15,textColor=INK,spaceAfter=7),
 'small':ParagraphStyle('small',fontName='A',fontSize=8.8,leading=12.1,textColor=GRAY),
 'head':ParagraphStyle('head',fontName='AB',fontSize=12.4,leading=17,textColor=TEAL),
 'title':ParagraphStyle('title',fontName='AB',fontSize=24,leading=29,textColor=NAVY),
 'cell':ParagraphStyle('cell',fontName='A',fontSize=9,leading=12.3,textColor=INK),
}
def rich(t):
    s=escape(str(t)).replace('\n','<br/>')
    return re.sub(r'([\u3000-\u30ff\u3400-\u9fff\uff00-\uffef]+)',r'<font name="JP">\1</font>',s)

ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
tests=[]; assemblies=[]
for p in sorted((E/'test-results').glob('*.trx')):
    r=ET.parse(p).getroot(); rows=r.findall('.//t:UnitTestResult',ns)
    meth=r.find('.//t:TestMethod',ns)
    asm=meth.get('codeBase','').replace('\\','/').split('/')[-1].replace('OokiGrader.','').replace('.dll','')
    counts=Counter(x.get('outcome') for x in rows)
    assemblies.append([asm,str(counts['Passed']),str(counts['Failed']),str(counts['NotExecuted'])])
    for x in rows: tests.append({'assembly':asm,'name':x.get('testName'),'outcome':x.get('outcome'),'duration':x.get('duration')})
(E/'automated-test-inventory.json').write_text(json.dumps(tests,ensure_ascii=False,indent=2),encoding='utf-8')
assert sum(int(r[1]) for r in assemblies)==1078
assert sum(int(r[2]) for r in assemblies)==0
assert sum(int(r[3]) for r in assemblies)==7
ftext=(E/'frontend-test.log').read_text(encoding='utf-8-sig')
assert '178 passed' in ftext
zip_summary={}
with zipfile.ZipFile(E/'bulk-result.zip') as z:
    zip_summary['crcVerified']=z.testzip() is None
    zip_summary['entries']=z.namelist()
    zip_summary['pdfPages']={n:len(PdfReader(io.BytesIO(z.read(n))).pages) for n in z.namelist() if n.lower().endswith('.pdf')}
    zip_summary['manifest']=z.read(next(n for n in z.namelist() if n.endswith('manifest.csv'))).decode('utf-8-sig')
(E/'bulk-zip-validation.json').write_text(json.dumps(zip_summary,ensure_ascii=False,indent=2),encoding='utf-8')
pdf_checks={}
for name in ['practice-result.pdf','practice-result-corrected.pdf','retention-original-still-present.pdf']:
    p=E/name; r=PdfReader(p); text='\n'.join(x.extract_text() for x in r.pages)
    pdf_checks[name]={'pages':len(r.pages),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'textCharacters':len(text),'containsAlice':'Alice' in text,'containsBob':'Bob' in text,'contains20':'20' in text,'contains30':'30' in text}
    (E/(p.stem+'-extracted.txt')).write_text(text,encoding='utf-8')
assert pdf_checks['practice-result-corrected.pdf']['containsAlice']
assert not pdf_checks['practice-result-corrected.pdf']['containsBob']
assert pdf_checks['retention-original-still-present.pdf']['sha256']=='416a98dcd7498d5395b2d2cd9e57615ffeffbdc71298e6c0dde972b7837482a6'
(E/'pdf-validation.json').write_text(json.dumps(pdf_checks,indent=2),encoding='utf-8')

chapters=json.loads(DATA.read_text(encoding='utf-8'))
cv=canvas.Canvas(str(OUT),pagesize=(W,H),pageCompression=1)
cv.setTitle('Ooki Grader - Real-Life Workflow and Test Guide - 19 September 2026')
cv.setAuthor('Ooki Grader testing record / Codex')
page_index=[]; overflow=[]
def text(t,x,y,width,style='body'):
    p=Paragraph(rich(t),styles[style]); _,ht=p.wrap(width,H)
    p.drawOn(cv,x,y-ht)
    return y-ht
def newpage(title,tag='WORKFLOW GUIDE',anchor=None):
    if cv.getPageNumber()>1 or page_index: cv.showPage()
    page_index.append({'page':cv.getPageNumber(),'title':title})
    if anchor:
        cv.bookmarkPage(anchor); cv.addOutlineEntry(title,anchor,0)
    cv.setFillColor(TEAL); cv.rect(0,H-8,W,8,fill=1,stroke=0)
    cv.setFont('AB',8); cv.setFillColor(TEAL); cv.drawString(M,H-30,tag)
    y=text(title,M,H-45,CW,'title')-14
    cv.setStrokeColor(LINE); cv.line(M,29,W-M,29)
    cv.setFillColor(GRAY); cv.setFont('A',8)
    cv.drawString(M,16,'Ooki Grader | Real-life workflows | 19 September 2026 | Synthetic test data')
    cv.drawRightString(W-M,16,str(cv.getPageNumber()))
    return y
def block(label,items,x,y,width,numbered=False):
    y=text(label,x,y,width,'head')-6
    if isinstance(items,str):items=[items]
    for i,t in enumerate(items):
        prefix=f'{i+1}. ' if numbered else ''
        y=text(prefix+t,x,y,width)-8
    return y-7
def table(headers,rows,y,widths=None):
    widths=widths or [CW/len(headers)]*len(headers)
    data=[[Paragraph('<font color="white">'+rich(v)+'</font>',styles['cell']) for v in headers]]
    data += [[Paragraph(rich(v),styles['cell']) for v in row] for row in rows]
    t=Table(data,colWidths=widths,hAlign='LEFT',repeatRows=1)
    t.setStyle(TableStyle([('BACKGROUND',(0,0),(-1,0),NAVY),('ROWBACKGROUNDS',(0,1),(-1,-1),[WHITE,PALE]),('VALIGN',(0,0),(-1,-1),'TOP'),('LEFTPADDING',(0,0),(-1,-1),8),('RIGHTPADDING',(0,0),(-1,-1),8),('TOPPADDING',(0,0),(-1,-1),7),('BOTTOMPADDING',(0,0),(-1,-1),7),('LINEBELOW',(0,-1),(-1,-1),.5,LINE)]))
    _,ht=t.wrap(CW,H)
    if y-ht<40: overflow.append((cv.getPageNumber(),'table',y-ht))
    t.drawOn(cv,M,y-ht); return y-ht-15
def screen(shot,title,notes,crop=None,marks=None):
    p=E/'screenshots'/shot
    iw,ih=PILImage.open(p).size
    crop=crop or [260,60,iw-8,min(ih-12,1100)]
    x0,y0,x1,y1=crop; rw=x1-x0; rh=y1-y0
    y=newpage(title,'ANNOTATED SCREEN | INSTALLED 0.9.8 | '+shot[:2])
    top=y; maxh=H-205; scale=min(CW/rw,maxh/rh)
    if shot.startswith('10-'):
        # Split the narrow advanced-settings rail into two readable details.
        regions=[(1130,410,1485,870),(1130,870,1485,1270)]
        for k,(ax,ay,bx,by) in enumerate(regions,1):
            pw=(CW-24)/2; sc=min(pw/(bx-ax),maxh/(by-ay)); dw=(bx-ax)*sc;dh=(by-ay)*sc
            lx=M+(k-1)*(pw+24)+(pw-dw)/2;bt=top-dh
            cv.saveState();path=cv.beginPath();path.rect(lx,bt,dw,dh);cv.clipPath(path,stroke=0)
            cv.drawImage(str(p),lx-ax*sc,bt-(ih-by)*sc,width=iw*sc,height=ih*sc,mask='auto');cv.restoreState()
            cv.setStrokeColor(colors.HexColor('#FFBD43'));cv.setLineWidth(1.3);cv.rect(lx,bt,dw,dh,stroke=1,fill=0)
            cv.setFillColor(colors.HexColor('#FFBD43'));cv.circle(lx+10,top-10,8,stroke=0,fill=1)
            cv.setFillColor(NAVY);cv.setFont('AB',9);cv.drawCentredString(lx+10,top-13,str(k))
        yy=top-maxh-12
        for i,n in enumerate(notes):yy=text(f'{i+1}. '+n,M,yy,CW,'small')-5
        text('Two enlarged details from the same original screenshot; read left panel before right. Outlines are editorial annotations.',M,yy,CW,'small')
        return
    dw=rw*scale; dh=rh*scale; left=M+(CW-dw)/2; bottom=top-dh
    cv.saveState(); path=cv.beginPath();path.rect(left,bottom,dw,dh);cv.clipPath(path,stroke=0)
    cv.drawImage(str(p),left-x0*scale,bottom-(ih-y1)*scale,width=iw*scale,height=ih*scale,mask='auto')
    cv.restoreState();cv.setStrokeColor(LINE);cv.rect(left,bottom,dw,dh,fill=0,stroke=1)
    marks=marks or []
    for k,rect in enumerate(marks,1):
        ax,ay,bx,by=rect
        px=left+(ax-x0)*scale;py=bottom+(y1-by)*scale
        cv.setStrokeColor(colors.HexColor('#FFBD43'));cv.setLineWidth(1.4);cv.roundRect(px,py,(bx-ax)*scale,(by-ay)*scale,3,fill=0,stroke=1)
        cx=px+7;cy=py+(by-ay)*scale-7
        cv.setFillColor(colors.HexColor('#FFBD43'));cv.circle(cx,cy,8,fill=1,stroke=0)
        cv.setFillColor(NAVY);cv.setFont('AB',9);cv.drawCentredString(cx,cy-3,str(k))
    yy=bottom-12
    for i,n in enumerate(notes):
        yy=text((f'{i+1}. ' if i<len(marks) else '')+n,M,yy,CW,'small')-5
    yy=text('Actual screenshot; viewport cropped for legibility. Numbered outlines are editorial annotations. Original PNG retained in the evidence bundle.',M,yy,CW,'small')
    if yy<37:overflow.append((cv.getPageNumber(),'screenshot captions',yy))

y=newpage('Ooki Grader\nReal-life workflow & test guide','FIELD GUIDE + EVIDENCE | EDITION 2026-09-19',anchor='cover')
y=text('A detailed walkthrough for teachers, office staff and administrators',M,y,CW,'head')-18
left=M; col=(CW-28)/2; right=M+col+28
yl=block('What this guide contains',[
 'Daily classroom scenarios: maintain the roster, prepare tests, receive scans, identify students, grade, correct mistakes, finalize, export and review progress.',
 f"{len(list((E/'screenshots').glob('*.png')))} original screenshots, focused crops and numbered annotations; exact Japanese control names alongside English instructions.",
 'Administration, AI configuration, School Manager class reports, update retention, backups, recovery and troubleshooting.'],left,y,col)
yr=block('Verified test snapshot',[
 '1,256 automated tests passed: 1,078 backend and 178 frontend. Seven backend tests were skipped. Production frontend build and generated API contract check passed.',
 '38 additional live API checks passed across input guardrails, roles, credentials, sessions, archive/restore and maintenance. Browser actions covered roster changes, grading, exports and correction recovery.',
 'Installed app: 0.9.8. Source tested: ddb6836, including thinking fix 93686af. The newer fixed package was not successfully installed.'],right,y,col)
yr=block('Read the limits before using this as acceptance',[
 'Live Gemini success, School Manager delivery and a populated-data upgrade remain unverified. Two UI defects were reproduced. A green unit-test run is not proof of those live workflows.'],right,yr,col)

y=newpage('How to read the evidence','SCOPE, VERSIONS AND HONEST LIMITS',anchor='evidence')
y=table(['Label','Meaning','What it does not establish'],[
 ['PASS - live','Performed against the installed app through the browser or authenticated API; outcome checked.','No claim that every edge case or scale was exercised.'],
 ['PASS - automated','Source-level test completed in the fresh 19 September run.','Does not validate provider availability or the installed binary.'],
 ['INSPECTED','Screen opened, source/docs examined, or a confirmation canceled.','The underlying destructive or external operation was not completed.'],
 ['BLOCKED / UNAVAILABLE','An environmental prerequisite or installed-version feature was absent.','Must not be counted as a pass.'],
 ['DEFECT','Expected and actual behavior differ with evidence.','The issue has not been fixed by writing this guide.'],
 ['DOCUMENTED','Operational instructions derived from current repository code and documentation.','No fabricated live screenshot or successful execution is implied.'],
 ],y,[120,330,CW-450])
y=text('All people and class records in these screenshots are synthetic. The paper fixture prints Test Alice / RET-001. It was intentionally assigned to Test Bob for a controlled mistake-recovery path and later corrected to Alice. Interim Bob PDFs are test evidence only; the final revised PDF identifies Alice.',M,y,CW)

y=newpage('Readiness verdict and known defects','READ BEFORE PRODUCTION USE',anchor='findings')
y=table(['Finding','Evidence and impact','Current disposition'],[
 ['F01 - class not saved','WF-104 was submitted with Workflow A. Its detail screen and API response have no class. UI sends classLabel; API expects schoolClass.','Reproduced. CSV schoolClass import works. UI create needs a fix; the same edit mapping is a source-level risk.'],
 ['F02 - misleading kanji badge','English results show 漢字ルール適用 while the result API says not_applicable. The page tests the nonempty string as a boolean.','Reproduced. The score remains 20/30; the label should not be trusted as proof a rule ran.'],
 ['Installed feature gap','Pass/fail matrix and School Manager URLs returned 200 text/html (the SPA shell), not JSON.','Unavailable in installed 0.9.8. Any earlier interpretation of HTTP 200 as API success is superseded by this check.'],
 ['Live AI / thinking','Installed Gemini 3.8 Flash connection displays gemini_request_invalid. LOW/MEDIUM source fix is tested; earlier fixed probe did not prove model availability.','Live AI extraction, name OCR, grading and adjudication are blocked. No key was exposed in this guide.'],
 ['Update / retention','UAC launch was canceled. Separate fixed-build instance launches were rejected by automatic approval review.','No completed populated-data upgrade. Original retained PDF hash still matches, which only proves the current baseline is intact.'],
 ],y,[132,365,CW-497])

toc_pages=(len(chapters)+19)//20
for n in range(toc_pages):
    y=newpage('Workflow directory'+(' - continued' if n else ''),'CLICKABLE CHAPTER LINKS')
    for c in chapters[n*20:(n+1)*20]:
        title=f"{c['id']:02d}  {c['title']}"
        cv.linkRect('',f"chapter{c['id']}",(M,y-19,W-M,y+3),relative=0,thickness=0)
        y=text(title,M,y,CW,'body')-6

for c in chapters:
    y=newpage(f"{c['id']:02d}  {c['title']}",c.get('status','DOCUMENTED'),anchor=f"chapter{c['id']}")
    col=(CW-30)/2
    yl=block('Real-life situation',c['scenario'],M,y,col)
    yl=block('Do this',c['steps'],M,yl,col,True)
    yr=block('Check the outcome',c['verify'],M+col+30,y,col)
    yr=block('If something goes wrong',c['recovery'],M+col+30,yr,col)
    yr=block('Test record / boundary',c['evidence'],M+col+30,yr,col)
    if min(yl,yr)<42: overflow.append((cv.getPageNumber(),c['title'],min(yl,yr)))
    for s in c.get('screens',[]):screen(s['file'],s['title'],s['notes'],s.get('crop'),s.get('marks'))

y=newpage('Automated tests: exact suite totals','FRESH RUN | 19 SEPTEMBER 2026',anchor='testtotals')
y=table(['Assembly / suite','Passed','Failed','Skipped'],assemblies+[['Frontend / 26 Vitest files','178','0','0'],['Total','1256','0','7']],y,[CW-210,70,70,70])
y=text('Commands: dotnet test OokiGrader.slnx --configuration Release --no-restore; npm test; npm run build; npm run api:check. Logs and original TRX files accompany this guide. Detailed test names and outcomes are in automated-test-inventory.json.',M,y,CW,'small')

y=newpage('Skipped tests and external coverage gaps','NOT COUNTED AS PASSES',anchor='skipped')
skips=[t for t in tests if t['outcome']=='NotExecuted']
for t in skips:
    y=text(t['assembly']+' / '+t['name'],M,y,CW,'small')-9
y=block('How to close the gaps',[
 'Supply the documented live-provider fixtures/credentials and run the corresponding opt-in tests. The exact prerequisites are in each test source; an available key alone does not establish model access.',
 'Install the fixed build through the updater, then prove successful capabilities and all four AI workflows using synthetic material. Record provider response, model ID, timing and teacher review.',
 'Use a dedicated School Manager dummy student and guardian. First run dry-run matching; attachment upload and actual receipt require a separately authorized live-send test.',
 'Complete update, failure rollback and isolated restore on a controlled Windows host. A current hash comparison before an update does not verify migration retention.'],M,y,CW)

y=newpage('Live API guardrail checks','EIGHT BOUNDED CHECKS | INSTALLED APP',anchor='guards')
guards=json.loads((E/'guardrail-checks.json').read_text(encoding='utf-8-sig'))
y=table(['Case','Observed HTTP status','Outcome'],[[g['name'],str(g['actualStatus']),'PASS' if g['passed'] else 'FAIL'] for g in guards],y,[CW-220,125,95])
y=text('Test-harness correction: the first run expected 409 for revision=0. Source inspection showed the correct response is 428 (missing valid precondition). A separate revision=1 stale update then returned 412. Both attempts were rejected without overwriting student data; the initial expectation is retained in the evidence.',M,y,CW)

for filename,title in [('staff-lifecycle-checks.json','Staff permissions and credential lifecycle'),('lifecycle-control-checks.json','Archive, maintenance and reception lifecycle')]:
    rows=json.loads((E/filename).read_text(encoding='utf-8-sig'))
    assert all(r['passed'] for r in rows)
    for offset in range(0,len(rows),12):
        y=newpage(title+(' - continued' if offset else ''),'PASS - LIVE AUTHENTICATED API CHECKS')
        y=table(['Case','Actual','Expected','Result'],[[r['name'],str(r['actual']),str(r['expected']),'PASS'] for r in rows[offset:offset+12]],y,[CW-265,115,95,55])
        if filename.startswith('lifecycle'):
            text('Harness corrections retained in evidence: template state is lifecycleState, and If-Match uses a quoted rev-N ETag. An old If-Match carried in the PowerShell session caused one correctly rejected 412; clearing the stale header allowed the intended restore. The completed fixture was left archived, maintenance off and practice reception closed.',M,y,CW,'small')

y=newpage('Output validation and retained baseline','PDF, ZIP AND FINAL APPLICATION STATE',anchor='outputs')
y=table(['Artifact / state','Verified result'],[
 ['Individual PDF before correction',f"{pdf_checks['practice-result.pdf']['pages']} page(s), parsed successfully; Test Bob at that intermediate stage."],
 ['Individual PDF after correction',f"{pdf_checks['practice-result-corrected.pdf']['pages']} page(s); Test Alice present, Bob absent, 20 and 30 present. Regenerated after result revision 11."],
 ['Bulk ZIP',f"CRC validation passed; {len(zip_summary['pdfPages'])} PDF(s) plus manifest.csv. UI displayed verified completion, 1/1."],
 ['Original retention PDF','SHA-256 416a98dcd7498d5395b2d2cd9e57615ffeffbdc71298e6c0dde972b7837482a6; matches the saved pre-update fixture exactly.'],
 ['Final practice state','Assigned to Test Alice / RET-001, 20/30, finalized. Practice reception closed. Original 18 September result preserved.'],
 ['Roster and test cleanup','Seven synthetic students. Riku is active with alias retained and missing class as reproduced. QA staff is disabled; the unused test template is archived; maintenance is off.'],
 ],y,[180,CW-180])
y=text('The bundled earlier Bob PDF must not be used as a current report. It demonstrates why a recipient correction requires a new export. No School Manager attachment or message was sent.',M,y,CW)

coverage=[]
for c in chapters:coverage.append([f"{c['id']:02d}",c['title'],c['status']])
for j in range(0,len(coverage),14):
    y=newpage('Feature coverage matrix'+(' - continued' if j else ''),'EVERY DOCUMENTED AREA HAS A DISPOSITION')
    table(['Chapter','Feature area','Evidence level'],coverage[j:j+14],y,[65,400,CW-465])

y=newpage('Technical evidence and source map','REPRODUCIBLE REFERENCES',anchor='sources')
refs=[
 ('Source revision','https://github.com/toshizo-link/upgraded-ooki-grader/tree/ddb6836'),
 ('Operations guide','https://github.com/toshizo-link/upgraded-ooki-grader/blob/ddb6836/docs/operations/host-app-setup-and-operations-ja.md'),
 ('UX specification','https://github.com/toshizo-link/upgraded-ooki-grader/blob/ddb6836/docs/specification/06-ux-specification.md'),
 ('Class field contract','https://github.com/toshizo-link/upgraded-ooki-grader/blob/ddb6836/src/OokiGrader.Contracts/ApiContracts.cs#L65'),
 ('Result badge rendering','https://github.com/toshizo-link/upgraded-ooki-grader/blob/ddb6836/src/OokiGrader.Web/src/pages/ResultDetailPage.tsx#L664'),
 ('School Manager API','https://github.com/toshizo-link/upgraded-ooki-grader/blob/ddb6836/src/OokiGrader.Host/Api/SchoolManagerAdminEndpoints.cs'),
 ('Reports / class matrix','https://github.com/toshizo-link/upgraded-ooki-grader/blob/ddb6836/src/OokiGrader.Host/Api/ReportsEndpoints.cs'),
]
for label,url in refs:
    y=text(label,M,y,CW,'head')-3
    cv.linkURL(url,(M,y-24,W-M,y+4),relative=0,thickness=0)
    y=text(url,M,y,CW,'small')-13
y=text('The repository is the primary source for version-specific instructions. Screenshots are current installed-app evidence, not images generated from the specification. No public provider-price or current model-availability claim is made here.',M,y,CW)

y=newpage('Evidence package contents and next acceptance run','HANDOFF',anchor='handoff')
yl=block('Evidence files',[
 f"screenshots/: all {len(list((E/'screenshots').glob('*.png')))} original PNG captures. Matching numbered .txt files contain accessibility/DOM snapshots for reproducibility.",
 'test-results/: original TRX files. automated-test-inventory.json: every backend case and outcome. Frontend, production build and API-check logs are included.',
 'JSON results: roster import preview/apply/repeat, class-field defect, alias, API feature availability, guardrails, practice lifecycle, PDF/ZIP validation.',
 'Synthetic fixtures and sample reports: CSV, bulk ZIP, original retention PDF and corrected practice PDF. Interim exports are marked historical test evidence.',
 'Credentials, auth cookies, DPAPI blobs and credential-reader helpers are excluded from distribution.'],M,y,(CW-30)/2)
yr=block('Still required for full release acceptance',[
 'Fix and regress the student class mapping and misleading kanji badge; confirm class survives create/edit/reload and aligns with recipient matching.',
 'Complete a populated-data updater run; compare IDs, counts, aliases, scores, PDF/object hashes and encrypted key usability after update and restart.',
 'Prove Gemini 3.8 Flash capability probing plus template generation, name reading, initial grading and adjudication with the installed fixed build.',
 'Prove whole-class School Manager anonymization, dry run, attachment upload and actual dummy-guardian receipt. Test same-day coalescing, next-day release and unknown-send handling.',
 'Complete configured-backup verification and isolated restore; exercise separate-role browser screens, native scanner/file-picker paths, multi-page boundary cases, and controlled failure recovery.'],M+(CW-30)/2+30,y,(CW-30)/2)
if min(yl,yr)<40:overflow.append((cv.getPageNumber(),'handoff',min(yl,yr)))
cv.save()
(E/'guide-page-index.json').write_text(json.dumps(page_index,ensure_ascii=False,indent=2),encoding='utf-8')
(E/'guide-layout-check.json').write_text(json.dumps({'pages':len(page_index),'overflow':overflow},indent=2),encoding='utf-8')
if overflow:raise RuntimeError('Layout overflow: '+str(overflow))
reader=PdfReader(OUT)
assert len(reader.pages)==len(page_index)
assert all(p.extract_text().strip() for p in reader.pages)
print(json.dumps({'pdf':str(OUT),'pages':len(reader.pages),'bytes':OUT.stat().st_size,'screenshots':len(list((E/'screenshots').glob('*.png'))),'sha256':hashlib.sha256(OUT.read_bytes()).hexdigest()},indent=2))
