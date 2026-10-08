using MyTunnel.Gateway;

var app = GatewayApplication.Create(args);
await GatewayApplication.MigrateIfConfiguredAsync(app);
await app.RunAsync();
