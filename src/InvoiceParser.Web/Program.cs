using InvoiceParser.Core.Services;
using InvoiceParser.Core.Services.ML;
using InvoiceParser.Infrastructure.Data;
using InvoiceParser.Infrastructure.Repositories;
using InvoiceParser.Infrastructure.Services;
using InvoiceParser.Web.Configuration;
using InvoiceParser.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var mvcBuilder = builder.Services.AddControllersWithViews();
if (builder.Environment.IsDevelopment())
    mvcBuilder.AddRazorRuntimeCompilation();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.CommandTimeout(120)));

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.LogoutPath = "/Login/Logout";
        options.AccessDeniedPath = "/Login/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddScoped<IAuthService, IPathAuthService>();

// IPath is a remote server — use a longer timeout to handle slow network/server responses.
builder.Services.AddDbContext<IPathDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("IPathConnection"),
        sql => sql.CommandTimeout(180)));

var openAiSettings = builder.Configuration.GetSection("OpenAi").Get<OpenAiSettings>() ?? new OpenAiSettings();
builder.Services.AddSingleton(openAiSettings);

builder.Services.Configure<InvoiceParserPythonSettings>(
    builder.Configuration.GetSection(InvoiceParserPythonSettings.SectionName));
builder.Services.AddHttpClient(PythonInvoiceIntegrationService.HttpClientName, (sp, client) =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<InvoiceParserPythonSettings>>().Value;
    if (Uri.TryCreate(opt.BaseUrl, UriKind.Absolute, out var uri))
        client.BaseAddress = uri;
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddScoped<PythonInvoiceIntegrationService>();
builder.Services.AddScoped<IInvoicePythonAugmenter>(sp => sp.GetRequiredService<PythonInvoiceIntegrationService>());

builder.Services.AddHttpClient<AiInvoiceParser>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddHttpClient<FeedbackAgent>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(3);
});

builder.Services.AddHttpClient<ChargeValidationService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});

builder.Services.AddSingleton<TrainingDataBuilder>();
builder.Services.AddSingleton<InvoiceMLService>(sp =>
{
    var dataBuilder = sp.GetRequiredService<TrainingDataBuilder>();
    var logger = sp.GetRequiredService<ILogger<InvoiceMLService>>();
    var modelPath = Path.Combine(builder.Environment.ContentRootPath, "ml-model", "invoice-field-model.zip");
    return new InvoiceMLService(dataBuilder, logger, modelPath);
});

// --- Line Classification ML Pipeline ---
var trainingDataPath = Path.Combine(builder.Environment.ContentRootPath, "TrainingData");
var modelsPath = Path.Combine(builder.Environment.ContentRootPath, "Models");

builder.Services.AddSingleton<ITrainingDataLoader>(sp =>
    new TrainingDataLoader(trainingDataPath, sp.GetRequiredService<ILogger<TrainingDataLoader>>()));

builder.Services.AddSingleton<IInvoiceMLTrainer, InvoiceMLTrainer>();

builder.Services.AddSingleton<IFeedbackLookupService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<FeedbackLookupService>>();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    return new FeedbackLookupService(logger, scopeFactory);
});

builder.Services.AddSingleton<IInvoicePredictionService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<InvoicePredictionService>>();
    var feedbackLookup = sp.GetRequiredService<IFeedbackLookupService>();
    var latestModelPath = Path.Combine(modelsPath, "invoice_model_latest.zip");
    return new InvoicePredictionService(logger, latestModelPath, feedbackLookup);
});

builder.Services.AddSingleton<IModelRetrainingService>(sp =>
{
    var loader = sp.GetRequiredService<ITrainingDataLoader>();
    var trainer = sp.GetRequiredService<IInvoiceMLTrainer>();
    var predictor = sp.GetRequiredService<IInvoicePredictionService>();
    var logger = sp.GetRequiredService<ILogger<ModelRetrainingService>>();
    return new ModelRetrainingService(loader, trainer, predictor, logger, modelsPath);
});

builder.Services.AddScoped<IFeedbackTrainingDataService, FeedbackTrainingDataService>();
builder.Services.AddHostedService<TrainingDataWatcherService>();

// --- OCR Service (Tesseract) ---
var tessDataPath = Path.Combine(builder.Environment.ContentRootPath, "tessdata");
builder.Services.AddSingleton<IOcrService>(sp =>
    new TesseractOcrService(sp.GetRequiredService<ILogger<TesseractOcrService>>(), tessDataPath));

builder.Services.AddScoped<IInvoiceRepository, InvoiceRepository>();
builder.Services.AddSingleton<ICarrierRuleStore, JsonCarrierRuleStore>();
builder.Services.AddScoped<PdfTextExtractorService>(sp =>
    new PdfTextExtractorService(
        sp.GetRequiredService<ILogger<PdfTextExtractorService>>(),
        sp.GetRequiredService<IOcrService>()));
builder.Services.AddScoped<GenericInvoiceParser>();
builder.Services.AddScoped<VerizonWirelessParser>();
builder.Services.AddScoped<RuleLearningService>();
builder.Services.AddScoped<FeedbackProcessor>();
builder.Services.AddScoped<InvoiceService>();

var app = builder.Build();

// Apply any pending EF Core migrations on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        db.Database.Migrate();
    }
    catch (Exception ex) when (ex.Message.Contains("already an object named"))
    {
        // The database was created before EF migrations were introduced.
        // Bootstrap the migrations history table, mark InitialCreate as already
        // applied (those tables exist), then Migrate() picks up any new ones.
        startupLogger.LogWarning(
            "Existing database detected with no migration history. " +
            "Bootstrapping __EFMigrationsHistory then applying pending migrations.");

        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (
                SELECT 1 FROM sys.tables
                WHERE name = '__EFMigrationsHistory' AND schema_id = SCHEMA_ID('dbo')
            )
            CREATE TABLE [dbo].[__EFMigrationsHistory] (
                [MigrationId]    nvarchar(150) NOT NULL,
                [ProductVersion] nvarchar(32)  NOT NULL,
                CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
            )");

        var initialCreate = db.Database.GetMigrations()
            .FirstOrDefault(m => m.EndsWith("_InitialCreate"));

        if (initialCreate is not null)
        {
            db.Database.ExecuteSqlRaw(
                "IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = {0}) " +
                "INSERT INTO [__EFMigrationsHistory] ([MigrationId],[ProductVersion]) VALUES ({0},{1})",
                initialCreate, "7.0.20");
        }

        db.Database.Migrate();
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
