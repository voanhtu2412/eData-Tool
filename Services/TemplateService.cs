using Microsoft.EntityFrameworkCore;
using TaxCodeCollector.Data;
using TaxCodeCollector.Models;

namespace TaxCodeCollector.Services;

public class TemplateService
{
    public async Task<List<FieldTemplate>> LoadTemplatesAsync()
    {
        await using var db = new AppDbContext();
        await db.Database.EnsureCreatedAsync();

        return await db.FieldTemplates
            .AsNoTracking()
            .OrderBy(x => x.OrderIndex)
            .ThenBy(x => x.Id)
            .ToListAsync();
    }

    public async Task<List<FieldTemplate>> SaveTemplatesAsync(IEnumerable<FieldTemplate> templates)
    {
        await using var db = new AppDbContext();
        await db.Database.EnsureCreatedAsync();

        var existing = await db.FieldTemplates.ToListAsync();
        db.FieldTemplates.RemoveRange(existing);

        var normalized = templates
            .Where(x => !string.IsNullOrWhiteSpace(x.FieldName))
            .Select((x, index) => new FieldTemplate
            {
                FieldName = x.FieldName.Trim(),
                CssSelector = x.CssSelector.Trim(),
                XPath = x.XPath.Trim(),
                SampleValue = x.SampleValue.Trim(),
                OrderIndex = index + 1
            })
            .ToList();

        db.FieldTemplates.AddRange(normalized);
        await db.SaveChangesAsync();
        return normalized;
    }
}
