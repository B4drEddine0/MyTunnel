var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Hello from my local application!");

app.MapGet("/info", () => Results.Ok(new
{
    application = "MyTunnel.LocalServer",
    message = "This application is running on the developer's machine."
}));

app.Run();