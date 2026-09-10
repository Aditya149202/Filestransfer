namespace OrderService.DTOs;

public record OrderItemResponse(int DrugId, int Quantity, decimal UnitPriceAtOrder);




using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OrderService.Auth;
using OrderService.Clients;
using OrderService.Data;
using OrderService.Middleware;
using OrderService.Repositories.Implementations;
using OrderService.Repositories.Interfaces;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ---------- EF Core ----------
builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("OrdersDb")));

// ---------- Repositories ----------
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IPaymentIntentRepository, PaymentIntentRepository>();

// ---------- Services ----------
// Fully-qualified on the implementation side — "OrderService" is both the project's
// root namespace and this class's name.
builder.Services.AddScoped<IOrderService, OrderService.Services.OrderService>();

// ---------- Internal service-to-service client ----------
// Shared-key auth, not JWT-forwarding — see prior decision. Attached once here at
// HttpClient configuration time so it works identically for controller-triggered
// calls and background-job-triggered calls (e.g. stale-order auto-cancel).
builder.Services.AddHttpClient<ISupplierInventoryClient, SupplierInventoryClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["SupplierInventoryService:BaseUrl"]!);
    client.DefaultRequestHeaders.Add("X-Internal-Api-Key", builder.Configuration["InternalApi:Key"]);
});

// ---------- JWT (RS256, validate-only — OrderService never signs) ----------
var rsa = RSA.Create();
rsa.ImportFromPem(builder.Configuration["Jwt:PublicKey"]!.ToCharArray());
var signingKey = new RsaSecurityKey(rsa);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            IssuerSigningKey = signingKey,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };

        // Auth failures bypass GlobalExceptionMiddleware entirely (nothing is thrown),
        // so 401/403 need their own ProblemDetails shaping — same pattern as
        // UserAuthService/SupplierInventoryService. Duplicated across services by
        // design-not-yet-consolidated, per the existing flag on that pattern.
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                var problem = new
                {
                    type = "https://tools.ietf.org/html/rfc7807",
                    title = "Unauthorized",
                    status = 401,
                    detail = "A valid bearer token is required."
                };
                return context.Response.WriteAsJsonAsync(problem);
            },
            OnForbidden = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/problem+json";
                var problem = new
                {
                    type = "https://tools.ietf.org/html/rfc7807",
                    title = "Forbidden",
                    status = 403,
                    detail = "You do not have permission to perform this action."
                };
                return context.Response.WriteAsJsonAsync(problem);
            }
        };
    })
    // Second scheme: authenticates OrderService's own /internal/* callers (currently
    // none inbound — SupplierInventoryService is the one exposing /internal/*. Kept
    // here only if OrderService ever exposes its own internal endpoints later;
    // remove if it never does, to avoid registering an unused scheme.
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationOptions.SchemeName, options => { });

builder.Services.AddAuthorization();

// ---------- Controllers ----------
builder.Services.AddControllers();

// ---------- Swagger ----------
// Swashbuckle 10.x-compatible syntax — Microsoft.OpenApi namespace, not
// Microsoft.OpenApi.Models, per the breaking change already worked around
// in UserAuthService/SupplierInventoryService.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT access token."
    };
    options.AddSecurityDefinition("Bearer", securityScheme);
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = Array.Empty<string>()
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<GlobalExceptionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
