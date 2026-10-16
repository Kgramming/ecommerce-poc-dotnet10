using MongoDB.Driver;
using Y3L.Supply.Api;
using Y3L.Supply.Api.Repos;
using Y3L.Supply.Api.Services;

var builder = WebApplication.CreateBuilder(args);

static string Env(string key, string def) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : def;

// ---- configuration (env vars, Mac-friendly defaults) ----
var pgConn = $"Host={Env("PGHOST", "localhost")};Port={Env("PGPORT", "5435")};" +
             $"Database={Env("PGDATABASE", "ecommerce")};Username={Env("PGUSER", "ecommerce")};" +
             $"Password={Env("PGPASSWORD", "ecommerce")}";
var mongoUri = $"mongodb://{Env("MONGOHOST", "localhost")}:{Env("MONGOPORT", "27017")}";
var mongoDb = Env("MONGODB", "ecommerce");
var esUrl = $"http://{Env("ESHOST", "localhost")}:{Env("ESPORT", "9200")}";
var esIndex = Env("ESINDEX", "orders");
var syncStrategy = Env("SYNC_STRATEGY", "dual_write");

builder.WebHost.UseUrls("http://0.0.0.0:8002");

builder.Services.AddControllers();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173", "http://localhost:5174", "http://localhost:5175")
    .AllowCredentials()
    .AllowAnyMethod()
    .AllowAnyHeader()));

// ---- dependencies ----
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoUri));
builder.Services.AddSingleton(_ => new ProductRepo(
    _.GetRequiredService<IMongoClient>(), mongoDb));
builder.Services.AddSingleton(_ => new OrderRepo(pgConn));
builder.Services.AddSingleton(_ => new EsClient(esUrl));
builder.Services.AddSingleton(sp =>
    new SyncService(sp.GetRequiredService<EsClient>(), esIndex,
        sp.GetRequiredService<ILogger<SyncService>>()));
builder.Services.AddSingleton(sp =>
    new OrderService(sp.GetRequiredService<OrderRepo>(),
        sp.GetRequiredService<ProductRepo>(), sp.GetRequiredService<SyncService>(),
        syncStrategy, sp.GetRequiredService<ILogger<OrderService>>()));
builder.Services.AddSingleton(sp =>
    new PollWorker(sp.GetRequiredService<OrderRepo>(),
        sp.GetRequiredService<SyncService>(), syncStrategy,
        sp.GetRequiredService<ILogger<PollWorker>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<PollWorker>());

var app = builder.Build();

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseCors();
app.MapControllers();

// ensure the ES index exists (never fail boot because ES is napping)
await app.Services.GetRequiredService<SyncService>().EnsureIndexAsync();

app.Run();
