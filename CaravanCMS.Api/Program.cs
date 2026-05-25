using CaravanCMS.Api;
using CaravanCMS.Api.Data;
using CaravanCMS.Api.Middleware;
using CaravanCMS.Api.Services;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using Serilog;
using Serilog.Events;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // ── Serilog bootstrap (before anything else so startup errors are captured) ──
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: "logs/caravan-cms-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} — {Message:lj}{NewLine}{Exception}")
            .CreateBootstrapLogger();

        try
        {
            Log.Information("CaravanCMS API starting up");

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

            // ── Serilog ────────────────────────────────────────────────────────
            builder.Host.UseSerilog((ctx, services, configuration) => configuration
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command",
                    ctx.HostingEnvironment.IsDevelopment() ? LogEventLevel.Debug : LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    path: "logs/caravan-cms-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30));

            // ── Database ───────────────────────────────────────────────────────
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                    ?? "Data Source=caravan-cms.db"));

            // ── Application services ───────────────────────────────────────────
            builder.Services.AddScoped<ExcelImportService>();
            builder.Services.AddScoped<FileScanner>();
            builder.Services.AddScoped<FuzzyMatcher>();

            // ── Controllers ────────────────────────────────────────────────────
            builder.Services.AddControllers();

            // ── Swagger/OpenAPI ────────────────────────────────────────────────
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v1", new()
                {
                    Title = "CaravanCMS API",
                    Version = "v1",
                    Description = "REST API for Caravanland's caravan service management system. " +
                                  "All endpoints require the X-API-Key header."
                });
                options.AddSecurityDefinition("ApiKey", new()
                {
                    Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
                    In = Microsoft.OpenApi.Models.ParameterLocation.Header,
                    Name = "X-API-Key",
                    Description = "API key for authentication"
                });
                options.AddSecurityRequirement(new()
                {
                    {
                        new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                        {
                            Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "ApiKey" }
                        },
                        Array.Empty<string>()
                    }
                });

                string coreXml = Path.Combine(AppContext.BaseDirectory, "CaravanCMS.Core.xml");
                string apiXml  = Path.Combine(AppContext.BaseDirectory, "CaravanCMS.Api.xml");
                if (File.Exists(coreXml)) options.IncludeXmlComments(coreXml);
                if (File.Exists(apiXml))  options.IncludeXmlComments(apiXml);
            });

            // ── CORS ───────────────────────────────────────────────────────────
            builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
                p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

            // ── File upload size limit ─────────────────────────────────────────
            int maxMb = builder.Configuration.GetValue<int>("CaravanCMS:MaxUploadSizeMB", 100);
            builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
            {
                o.MultipartBodyLengthLimit = maxMb * 1024L * 1024L;
            });
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Limits.MaxRequestBodySize = maxMb * 1024L * 1024L;
            });

            WebApplication app = builder.Build();

            // ── Auto-create database on startup ────────────────────────────────
            using (IServiceScope scope = app.Services.CreateScope())
            {
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Database.EnsureCreated();
                ApplyColumnAdditions(db);
                Log.Information("Database ready at: {DbPath}", db.Database.GetDbConnection().DataSource);
            }

            // ── Middleware pipeline ────────────────────────────────────────────
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "CaravanCMS API v1");
                c.RoutePrefix = "swagger";
                c.DocumentTitle = "CaravanCMS API";
            });

            app.UseCors();
            app.UseSerilogRequestLogging(o =>
            {
                o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} → {StatusCode} in {Elapsed:0.0}ms";
                o.GetLevel = (ctx, elapsed, ex) =>
                    ex != null || ctx.Response.StatusCode > 499
                        ? LogEventLevel.Error
                        : LogEventLevel.Debug;
            });

            app.UseMiddleware<ApiKeyMiddleware>();
            app.MapControllers();

            app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

            Log.Information("CaravanCMS API ready — launching system tray");

            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new TrayApplicationContext(app));
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "CaravanCMS API terminated unexpectedly during startup");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Adds columns that didn't exist when the database was first created.
    // SQLite has no IF NOT EXISTS for ALTER TABLE, so we check the column list first.
    static void ApplyColumnAdditions(ApplicationDbContext db)
    {
        using Microsoft.Data.Sqlite.SqliteConnection conn = new(db.Database.GetConnectionString());
        conn.Open();

        HashSet<string> caravanCols = GetColumns(conn, "Caravans");

        if (!caravanCols.Contains("SelfContainment"))
            Execute(conn, "ALTER TABLE Caravans ADD COLUMN SelfContainment TEXT NULL");

        if (!caravanCols.Contains("SelfContainmentDue"))
            Execute(conn, "ALTER TABLE Caravans ADD COLUMN SelfContainmentDue TEXT NULL");
    }

    static HashSet<string> GetColumns(Microsoft.Data.Sqlite.SqliteConnection conn, string table)
    {
        HashSet<string> cols = new(StringComparer.OrdinalIgnoreCase);
        using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using Microsoft.Data.Sqlite.SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            cols.Add(reader.GetString(1));
        return cols;
    }

    static void Execute(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
