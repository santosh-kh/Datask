﻿using System.Data;
using System.Text.Json;

using Datask.Common.Utilities;
using Datask.Providers.Schemas;
using Datask.Tool.ExcelData.Core.Generators.CSharpHelpers.Templates;

using DotLiquid;

using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace Datask.Tool.ExcelData.Core.Generators.CSharpHelpers;

public sealed class CSharpHelperGenerator : GeneratorBase<CSharpHelperGeneratorOptions, StatusEvents>
{
    static CSharpHelperGenerator()
    {
        RegisterTypes();
    }

    public CSharpHelperGenerator(CSharpHelperGeneratorOptions options)
        : base(options)
    {
    }

    public override async Task ExecuteAsync()
    {
        if (Options.Flavors is null or { Count: 0 })
            return;

        string filePath = Options.FilePath;
        FileHelpers.EnsureDirectoryExists(filePath);

        await using FileStream outputFile = File.Create(filePath);
        await using StreamWriter writer = new(outputFile);

        await writer.WriteAsync(RenderTemplate(await CSharpHelperTemplates.PopulateDataTemplate, Options))
            .ConfigureAwait(false);

        foreach (Flavor flavor in Options.Flavors)
        {
            FireStatusEvent(StatusEvents.Generate,
                "Generating data helper for {Flavor} information.",
                new { Flavor = flavor.Name });

            await writer.WriteAsync(RenderTemplate(await CSharpHelperTemplates.PopulateFlavorDataTemplate,
                    Options.Flavors.Count > 1 ? flavor.Name : "Default"))
                .ConfigureAwait(false);

            await using FileStream excelFile = new(flavor.ExcelFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read);
            IWorkbook workbook = new XSSFWorkbook(excelFile);

            int worksheetCount = workbook.NumberOfSheets;
            await PopulateConsolidatedData(writer, workbook, worksheetCount).ConfigureAwait(false);

            for (int index = 0; index < worksheetCount; index++)
            {
                var worksheet = (XSSFSheet)workbook.GetSheetAt(index);

                List<XSSFTable> worksheetTables = worksheet.GetTables();
                if (worksheetTables.Count is 0 or > 1 || worksheet.LastRowNum == 0)
                    continue;

                string[] tableName = worksheetTables[0].DisplayName.Split('.');
                if (tableName.Length is < 1 or > 2)
                    continue;

                TableBindingModel td = new(tableName[1], tableName[0]);

                UpdateTableModelFromExcel(worksheet, td, out int cellCount);

                IList<List<string?>> dataRows = FillDataRows(worksheet, td, cellCount);
                IList<string> fullRows = dataRows.Select(r => string.Join(", ", r)).ToList();

                //Remove autogenerated columns like timestamp
                foreach (ColumnBindingModel col in td.Columns.Where(c => c.IsAutoGenerated).ToList())
                    td.Columns.Remove(col);

                flavor.TableDefinitions.Add(td);

                await writer.WriteAsync(RenderTemplate(await CSharpHelperTemplates.PopulateTableDataTemplate,
                    new
                    {
                        table = td,
                        dr = dataRows,
                        fullRows,
                        has_identity_column = td.Columns.Any(c => c.IsIdentity)
                    })).ConfigureAwait(false);
            }

            await writer.WriteAsync('}').ConfigureAwait(false);
        }

        await writer.WriteAsync('}').ConfigureAwait(false);
    }

    private async Task PopulateConsolidatedData(StreamWriter writer, IWorkbook xssWorkbook, int worksheetCount)
    {
        IList<TableBindingModel> tables = new List<TableBindingModel>();
        for (int index = 0; index < worksheetCount; index++)
        {
            var sheet = (XSSFSheet)xssWorkbook.GetSheetAt(index);

            List<XSSFTable> xssfTables = sheet.GetTables();
            if (xssfTables.Count == 0 || sheet.LastRowNum == 0)
                continue;

            string[] tableName = xssfTables[0].DisplayName.Split('.');
            tables.Add(new TableBindingModel(tableName.Skip(1).First(), tableName.Take(1).First()));
        }

        await writer.WriteAsync(RenderTemplate(await CSharpHelperTemplates.PopulateConsolidatedDataTemplate,
                tables.Select(t => new { schema = t.Schema, name = t.Name }).ToList()))
            .ConfigureAwait(false);
    }


    private static void UpdateTableModelFromExcel(XSSFSheet worksheet, TableBindingModel td, out int cellCount)
    {
        IRow headerRow = worksheet.GetRow(0);
        cellCount = headerRow.LastCellNum;
        for (int cellIdx = 0; cellIdx < cellCount; cellIdx++)
        {
            ICell cell = headerRow.GetCell(cellIdx);
            if (cell is null || string.IsNullOrWhiteSpace(cell.ToString()))
                throw new DataskException(
                    $"Cell in worksheet '{worksheet.SheetName}' at index {cellIdx} could not be retrieved.");

            string cellMetadata = cell.CellComment.String.ToString();
            if (cellMetadata is null)
                throw new DataskException(
                    $"Cell in worksheet '{worksheet.SheetName}' at index {cellIdx} does not have the metadata comment.");

            Dictionary<string, object>? columnMetadata =
                JsonSerializer.Deserialize<Dictionary<string, object>>(cellMetadata);

            if (columnMetadata is null)
                throw new DataskException(
                    $"Cell in worksheet '{worksheet.SheetName}' at index {cellIdx} has an invalid metadata comment.");

            ColumnBindingModel column = new(cell.ToString())
            {
                DbType = GetMetadata("DbType", Enum.Parse<DbType>),
                DatabaseType = GetMetadata("DbType", o => $"DbType.{o}"),
                CSharpType = GetMetadata("Type", o => o),
                IsPrimaryKey = GetMetadata("IsPrimaryKey", Convert.ToBoolean),
                IsNullable = GetMetadata("IsNullable", Convert.ToBoolean),
                IsIdentity = GetMetadata("IsIdentity", Convert.ToBoolean),
                MaxLength = GetMetadata("MaxLength", Convert.ToInt32),
                IsAutoGenerated = GetMetadata("IsAutoGenerated", Convert.ToBoolean),
                NativeType = GetMetadata("NativeType", o => o),
            };

            column.ParameterSize = column.DbType switch
            {
                DbType.AnsiString or DbType.AnsiStringFixedLength
                    or DbType.String or DbType.StringFixedLength
                    or DbType.Binary or DbType.VarNumeric
                    or DbType.Xml => column.MaxLength <= 0 ? int.MaxValue : column.MaxLength,
                DbType.Boolean => sizeof(bool),
                DbType.Byte => sizeof(byte),
                DbType.SByte => sizeof(sbyte),
                DbType.Int16 => sizeof(short),
                DbType.UInt16 => sizeof(ushort),
                DbType.Int32 => sizeof(int),
                DbType.UInt32 => sizeof(uint),
                DbType.Int64 => sizeof(long),
                DbType.UInt64 => sizeof(ulong),
                DbType.Currency or DbType.Decimal => sizeof(decimal),
                DbType.Single => sizeof(float),
                DbType.Double => sizeof(double),
                DbType.Date or DbType.DateTime or DbType.DateTime2 or DbType.DateTimeOffset or DbType.Time
                    or DbType.Guid or DbType.Object => 1,
                _ => 1
            };

            td.Columns.Add(column);

            T GetMetadata<T>(string name, Func<string, T> converter)
            {
                if (!columnMetadata.TryGetValue(name, out object objectValue))
                    throw new DataskException(
                        $"Cell in worksheet '{worksheet.SheetName}' at index {cellIdx} does not have the {name} metadata.");

                return converter(objectValue.ToString());
            }
        }
    }

    private static IList<List<string?>> FillDataRows(XSSFSheet sheet, TableBindingModel td, int cellCount)
    {
        IList<List<string?>> dataRows = new List<List<string?>>();

        for (int i = sheet.FirstRowNum + 1; i <= sheet.LastRowNum; i++)
        {
            List<string?> rowList = new();
            IRow row = sheet.GetRow(i);
            if (row == null)
                continue;
            if (row.Cells.All(d => d.CellType == CellType.Blank))
                continue;

            for (int j = row.FirstCellNum; j < cellCount; j++)
            {
                //Skip timestamp column data
                if (td.Columns[j].IsAutoGenerated)
                    continue;

                rowList.Add(
                    ConvertObjectValToCSharpType(row.GetCell(j), td.Columns[j].DbType, td.Columns[j].NativeType, td.Columns[j].IsNullable));
            }

            if (rowList.Count > 0)
                dataRows.Add(rowList);
        }

        return dataRows;
    }

    private static string ConvertObjectValToCSharpType(object? rowValue, DbType columnType, string nativeType, bool isNullable)
    {
        if (rowValue is null || (isNullable && rowValue.ToString().Equals("NULL", StringComparison.OrdinalIgnoreCase)))
            return "null";

        return columnType switch
        {
            DbType.Binary => nativeType == "varbinary"
                ? $@"""{rowValue}"".ToCharArray().Select(c => (byte)c).ToArray()"
                : $"BitConverter.GetBytes(Convert.ToUInt64({rowValue}))",
            DbType.Boolean => rowValue.ToString() == "0" ? "false" : "true",
            DbType.AnsiString or DbType.AnsiStringFixedLength or DbType.String or DbType.StringFixedLength
                or DbType.Xml => $@"""{EscapeStringValue(rowValue.ToString())}""",
            DbType.Decimal or DbType.Single or DbType.Double
                or DbType.Int16 or DbType.Int32 or DbType.Int64 or DbType.Byte => rowValue.ToString(),
            DbType.DateTime or DbType.Date or DbType.Time or DbType.DateTime2 => $@"DateTime.Parse(""{rowValue}"")",
            DbType.DateTimeOffset => $@"DateTimeOffset.Parse((string)""{rowValue}"")",
            DbType.Guid => $@"new Guid((string)""{rowValue}"")",
            _ => $@"""{rowValue}"""
        };

        static string EscapeStringValue(string str)
        {
            return str
                .Replace(@"\", @"\\") // First, replace backslash with double backslash (should be first)
                .Replace(@"""", @"\""")
                .Replace("\n", @"\n")
                .Replace("\r", @"\r");
        }
    }

    private static string RenderTemplate(string templateContent, object modelData)
    {
        var template = Template.Parse(templateContent);
        return template.Render(Hash.FromAnonymousObject(new { model = modelData }));
    }

    private static void RegisterTypes()
    {
        Template.RegisterSafeType(typeof(Type), typeof(Type).GetProperties().Select(p => p.Name).ToArray());
        Template.RegisterSafeType(typeof(CSharpHelperGeneratorOptions),
            typeof(CSharpHelperGeneratorOptions).GetProperties().Select(p => p.Name).ToArray());
        Template.RegisterSafeType(typeof(Flavor), typeof(Flavor).GetProperties().Select(p => p.Name).ToArray());
        Template.RegisterSafeType(typeof(TableBindingModel),
            typeof(TableDefinition).GetProperties().Select(p => p.Name).ToArray());
        Template.RegisterSafeType(typeof(ColumnBindingModel),
            typeof(ColumnBindingModel).GetProperties().Select(p => p.Name).ToArray());
        Template.RegisterSafeType(typeof(ColumnDefinition),
            typeof(ColumnDefinition).GetProperties().Select(p => p.Name).ToArray());
    }
}
