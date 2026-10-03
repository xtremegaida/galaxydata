using GalaxyData.Web.Hosting;
using Microsoft.AspNetCore.Builder;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddGalaxyData();

WebApplication app = builder.Build();
app.UseGalaxyData();
app.MapGalaxyData();

await app.RunAsync();
