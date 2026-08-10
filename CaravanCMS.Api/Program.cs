using CaravanCMS.Api;
using CaravanCMS.Api.Data;
using CaravanCMS.Api.Middleware;
using CaravanCMS.Api.Services;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using Serilog;
using Serilog.Events;
using System.Threading;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Global\CaravanCMS.Api.SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        // ── Single-instance guard — must be the very first thing, before any config/DB/Kestrel
        // work, so a second launch never gets far enough to open the DB or bind a port ──────
        using Mutex singleInstanceMutex = new(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            System.Windows.Forms.MessageBox.Show(
                "CaravanCMS API is already running — check the system tray.",
                "CaravanCMS API",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
            return;
        }

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

            // ── Outlook add-in static assets (manifest, task pane) — no API key required,
            // Outlook fetches these directly and can't attach custom headers ──────
            app.UseStaticFiles();

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

            // ── Outlook add-in manifest — rendered from the template at request time so the
            // whole thing is driven by a single CaravanCMS:PublicBaseUrl setting ────────────
            app.MapGet("/addin/manifest.xml", async (IConfiguration config, IWebHostEnvironment env) =>
            {
                string baseUrl = config["CaravanCMS:PublicBaseUrl"]?.TrimEnd('/') ?? string.Empty;
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    return Results.Problem(
                        title: "CaravanCMS:PublicBaseUrl is not configured",
                        detail: "Set CaravanCMS:PublicBaseUrl in appsettings.json to the HTTPS address " +
                                "staff use to reach this API, then reload this URL.",
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                Microsoft.Extensions.FileProviders.IFileInfo file =
                    env.WebRootFileProvider.GetFileInfo("addin/manifest.template.xml");
                if (!file.Exists)
                {
                    return Results.Problem(
                        title: "Manifest template missing",
                        detail: "wwwroot/addin/manifest.template.xml was not found in the deployed app.",
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                using StreamReader reader = new(file.CreateReadStream());
                string xml = (await reader.ReadToEndAsync()).Replace("{{BASE_URL}}", baseUrl);
                return Results.Text(xml, "application/xml");
            });

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

        HashSet<string> documentCols = GetColumns(conn, "Documents");

        if (!documentCols.Contains("LinkMethod"))
            Execute(conn, "ALTER TABLE Documents ADD COLUMN LinkMethod TEXT NULL");

        CreateMissingTables(conn, db, "Conversations", "CommunicationLogs", "Tags", "ConversationTags");

        MigrateTagsToPerCustomerScope(conn);
    }

    // Scopes Tags to a single customer (adds CustomerId, replaces the old global UNIQUE(Name) index
    // with a composite UNIQUE(CustomerId, Name)) — a tag named e.g. "12v Wiring" on one customer's
    // conversation must never link to a same-named tag on a different customer's conversation.
    // Splits any tag that — from the older, unscoped schema — ended up linked to conversations for
    // more than one customer into one row per customer, rewiring ConversationTags to match, so no
    // existing tag data is lost or silently merged.
    static void MigrateTagsToPerCustomerScope(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        HashSet<string> tagCols = GetColumns(conn, "Tags");
        if (!tagCols.Contains("CustomerId"))
        {
            Execute(conn, "ALTER TABLE Tags ADD COLUMN CustomerId INTEGER NOT NULL DEFAULT 0");
            Log.Information("Added Tags.CustomerId column — migrating existing tags to per-customer scope");

            // For each tag, find every distinct customer it's currently linked to via ConversationTags.
            Dictionary<int, List<int>> tagToCustomers = new();
            using (Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT DISTINCT ct.TagsId, c.CustomerId
                    FROM ConversationTags ct
                    JOIN Conversations c ON c.Id = ct.ConversationsId
                    ORDER BY ct.TagsId, c.CustomerId";
                using Microsoft.Data.Sqlite.SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int tagId = reader.GetInt32(0);
                    int customerId = reader.GetInt32(1);
                    if (!tagToCustomers.TryGetValue(tagId, out List<int>? list))
                        tagToCustomers[tagId] = list = new List<int>();
                    list.Add(customerId);
                }
            }

            foreach ((int tagId, List<int> customerIds) in tagToCustomers)
            {
                // The first customer keeps the original tag row.
                Execute(conn, $"UPDATE Tags SET CustomerId = {customerIds[0]} WHERE Id = {tagId}");

                // Any additional customers sharing that tag get their own new row, with their
                // ConversationTags links rewired away from the original.
                for (int i = 1; i < customerIds.Count; i++)
                {
                    int customerId = customerIds[i];
                    string name = GetTagName(conn, tagId);
                    int newTagId = InsertTagCopy(conn, name, customerId);

                    using Microsoft.Data.Sqlite.SqliteCommand rewire = conn.CreateCommand();
                    rewire.CommandText = @"
                        UPDATE ConversationTags
                        SET TagsId = $newTagId
                        WHERE TagsId = $oldTagId
                          AND ConversationsId IN (SELECT Id FROM Conversations WHERE CustomerId = $customerId)";
                    rewire.Parameters.AddWithValue("$newTagId", newTagId);
                    rewire.Parameters.AddWithValue("$oldTagId", tagId);
                    rewire.Parameters.AddWithValue("$customerId", customerId);
                    rewire.ExecuteNonQuery();

                    Log.Information("Split shared tag {TagId} ({Name}) — customer {CustomerId} now uses new tag {NewTagId}",
                        tagId, name, customerId, newTagId);
                }
            }
        }

        // Old schema had a global UNIQUE index on Name alone — replace with the composite one.
        Execute(conn, "DROP INDEX IF EXISTS \"IX_Tags_Name\"");
        Execute(conn, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Tags_CustomerId_Name\" ON Tags (CustomerId, Name)");
    }

    static string GetTagName(Microsoft.Data.Sqlite.SqliteConnection conn, int tagId)
    {
        using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM Tags WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", tagId);
        return (string)cmd.ExecuteScalar()!;
    }

    static int InsertTagCopy(Microsoft.Data.Sqlite.SqliteConnection conn, string name, int customerId)
    {
        using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Tags (Name, CustomerId) VALUES ($name, $customerId); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$customerId", customerId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // Creates any tables added to the EF model since this database was first created, without touching
    // existing tables — so a DB from an older version of the app upgrades in place with no data loss.
    static void CreateMissingTables(Microsoft.Data.Sqlite.SqliteConnection conn, ApplicationDbContext db, params string[] tableNames)
    {
        HashSet<string> existingTables = GetTableNames(conn);
        string[] missing = tableNames.Where(t => !existingTables.Contains(t)).ToArray();
        if (missing.Length == 0) return;

        Log.Information("Creating new tables: {Tables}", string.Join(", ", missing));

        // Generate the full DDL from the current EF model, then execute only the statements
        // that create one of the missing tables (or an index/FK on one of them). This guarantees
        // the schema — including the auto-named many-to-many join table columns — matches the
        // model exactly, without hand-writing SQL that could drift from it.
        string script = db.Database.GenerateCreateScript();
        string[] statements = script
            .Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (string statement in statements)
        {
            string sql = statement.Trim();
            if (sql.Length == 0) continue;

            bool targetsMissingTable = missing.Any(t =>
                sql.Contains($"TABLE \"{t}\"", StringComparison.OrdinalIgnoreCase) ||
                sql.Contains($"ON \"{t}\"", StringComparison.OrdinalIgnoreCase));

            if (targetsMissingTable)
                Execute(conn, sql);
        }
    }

    static HashSet<string> GetTableNames(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        HashSet<string> tables = new(StringComparer.OrdinalIgnoreCase);
        using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using Microsoft.Data.Sqlite.SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables;
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
