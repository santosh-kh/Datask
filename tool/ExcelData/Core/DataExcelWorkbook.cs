﻿using Datask.Common.Utilities;

using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;

namespace Datask.Tool.ExcelData.Core;

/// <summary>
///     Provides an object model for an Excel workbook that contains database data.
/// </summary>
public sealed class DataExcelWorkbook : IDisposable
{
    private readonly IWorkbook _workbook;

    public DataExcelWorkbook(string filePath)
    {
        _workbook = new XSSFWorkbook(filePath);
    }

    public void Dispose()
    {
        _workbook.Close();
    }

    public IEnumerable<DataExcelTable> EnumerateTables()
    {
        for (int i = 0; i < _workbook.NumberOfSheets; i++)
        {
            var worksheet = (XSSFSheet)_workbook.GetSheetAt(i);

            List<XSSFTable> wsTables = worksheet.GetTables();
            if (wsTables.Count == 0)
                throw new InvalidOperationException($"Worksheet {worksheet.SheetName} does not contain a table.");
            if (wsTables.Count > 1)
                throw new InvalidOperationException($"Worksheet {worksheet.SheetName} has more than one tables.");

            yield return new DataExcelTable(wsTables[0]);
        }
    }
}

public sealed class DataExcelTable
{
    private readonly XSSFTable _table;
    private readonly Lazy<IList<DataExcelTableColumn>> _columns;

    internal DataExcelTable(XSSFTable table)
    {
        (Schema, TableName) = GetNames(table);
        _table = table;
        _columns = new Lazy<IList<DataExcelTableColumn>>(LoadColumns);
    }

    private (string Schema, string TableName) GetNames(XSSFTable table)
    {
        string[] parts = table.Name.Split(new[] { '.' }, 2, StringSplitOptions.None);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : throw new DataskException($"Excel table {table.DisplayName} has an invalid name.");
    }

    public string Schema { get; }

    public string TableName { get; }

    public IList<DataExcelTableColumn> Columns => _columns.Value;

    public IEnumerable<object?[]> EnumerateRows()
    {
        CellReference startRef = _table.GetStartCellReference();
        CellReference endRef = _table.GetEndCellReference();

        XSSFSheet worksheet = _table.GetXSSFSheet();

        for (int r = startRef.Row + 1; r <= endRef.Row; r++)
        {
            object?[] data = new object[Columns.Count];

            IRow row = worksheet.GetRow(r);
            for (int c = startRef.Col; c <= endRef.Col; c++)
            {
                int index = c - startRef.Col;
                if (index < row.Cells.Count)
                {
                    ICell cell = row.Cells[index];
                    data[index] = cell.ToString();
                }
                else
                    data[index] = null;
            }

            yield return data;
        }
    }

    private IList<DataExcelTableColumn> LoadColumns()
    {
        CellReference startRef = _table.GetStartCellReference();
        CellReference endRef = _table.GetEndCellReference();

        List<DataExcelTableColumn> columns = new(endRef.Col - startRef.Col + 1);

        IRow headerRow = _table.GetXSSFSheet().GetRow(startRef.Row);
        for (int colIdx = startRef.Col; colIdx < endRef.Col; colIdx++)
        {
            ICell headerCell = headerRow.GetCell(colIdx);
            columns.Add(new DataExcelTableColumn(headerCell));
        }

        return columns;
    }
}

public sealed class DataExcelTableColumn
{
    public DataExcelTableColumn(ICell headerCell)
    {
        Text = headerCell.StringCellValue;
    }

    public string Text { get; }
}
