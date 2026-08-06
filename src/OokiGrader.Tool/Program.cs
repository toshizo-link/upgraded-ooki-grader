using OokiGrader.Tool;

return await ToolApplication.RunAsync(
    args,
    Console.Out,
    Console.Error,
    CancellationToken.None);
