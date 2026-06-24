using ClosedXML.Excel;
using TaxCodeCollector.Models;

namespace TaxCodeCollector.Services;

public class ExcelExportService
{
    public Task ExportAsync(string filePath, IEnumerable<FieldTemplate> templates, IEnumerable<CompanyResultRow> rows)
    {
        return Task.Run(() =>
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Companies");

            var exportFields = templates
                .Where(x => !string.IsNullOrWhiteSpace(x.FieldName))
                .OrderBy(x => x.OrderIndex)
                .ToList();

            for (var i = 0; i < exportFields.Count; i++)
            {
                worksheet.Cell(1, i + 1).Value = exportFields[i].FieldName;
            }

            var rowIndex = 2;
            foreach (var row in rows)
            {
                for (var i = 0; i < exportFields.Count; i++)
                {
                    worksheet.Cell(rowIndex, i + 1).Value = row[exportFields[i].FieldName];
                }

                rowIndex++;
            }

            var usedRange = worksheet.RangeUsed();
            if (usedRange is not null)
            {
                usedRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                usedRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                usedRange.FirstRow().Style.Font.Bold = true;
                usedRange.FirstRow().Style.Fill.BackgroundColor = XLColor.FromHtml("#E8F0FE");
            }

            worksheet.Columns().AdjustToContents();
            workbook.SaveAs(filePath);
        });
    }
}
