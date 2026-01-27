using OpenRA.Converter.Core.Interfaces;
using OpenRA.Converter.Core.Models; // Add hozzá ezt a using-ot
using OpenRA.Converter.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
// Beolvassuk a ConverterSettings szekciót az appsettings.json-ból
builder.Services.Configure<ConverterSettings>(builder.Configuration.GetSection("ConverterSettings"));

// --- Service Registration ---

// 1. References (Truth Source)
builder.Services.AddSingleton<IReferenceRegistry, ReferenceRegistry>();
builder.Services.AddScoped<IReferenceIngestionService, ReferenceIngestionService>();

// 2. Decision Tree (Parsing & Logic)
builder.Services.AddScoped<IDecisionTreeService, DecisionTreeService>();

// 3. Synthesis (Code Generation)
builder.Services.AddScoped<ITraitSynthesisService, TraitSynthesisService>();
builder.Services.AddScoped<ICodeWriter, CSharpCodeWriter>();

// 4. YAML Generation
builder.Services.AddScoped<IYamlSynthesisService, YamlSynthesisService>();
builder.Services.AddScoped<IYamlCodeWriter, YamlCodeWriter>();

// 5. File System (ÚJ)
builder.Services.AddScoped<IFileWriterService, FileWriterService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ... (többi rész marad változatlan)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();