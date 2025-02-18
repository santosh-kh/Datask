﻿// Copyright (c) 2021 Jeevan James
// This file is licensed to you under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Data;

using Dapper;

using Datask.Providers.Schemas;
using Datask.Providers.SqlServer.Scripts;

using Microsoft.Data.SqlClient;

namespace Datask.Providers.SqlServer;

public sealed class SqlServerSchemaQueryProvider : SchemaQueryProvider<SqlConnection>
{
    public SqlServerSchemaQueryProvider(SqlConnection connection)
        : base(connection)
    {
    }

    protected override async Task<TableDefinitionCollection> GetTablesTask(GetTableOptions options)
    {
        // Get all table columns and foreign keys, if required.
        IEnumerable<dynamic>? allTableColumns = null;
        IEnumerable<dynamic>? allTableReferences = null;
        if (options.IncludeColumns)
        {
            string getAllTableColumnsScript = await Script.GetAllTableColumns().ConfigureAwait(false);
            allTableColumns = await Connection.QueryAsync(getAllTableColumnsScript).ConfigureAwait(false);

            if (options.IncludeForeignKeys)
            {
                string getAllTableReferencesScript = await Script.GetAllTableReferences().ConfigureAwait(false);
                allTableReferences = await Connection.QueryAsync(getAllTableReferencesScript).ConfigureAwait(false);
            }
        }

        // Get all tables.
        string getTablesScript = await Script.GetTables().ConfigureAwait(false);
        IEnumerable<dynamic> tables = await Connection.QueryAsync(getTablesScript).ConfigureAwait(false);

        List<TableDefinition> allTables = new();
        foreach (dynamic table in tables)
        {
            TableDefinition tableDefn = new(table.Name, table.Schema, GetFullTableName(table.Schema, table.Name));
            if (allTableColumns is not null)
                AssignColumns(tableDefn, allTableColumns);
            if (allTableReferences is not null)
                AssignReferences(tableDefn, allTableReferences);
            allTables.Add(tableDefn);
        }

        return new TableDefinitionCollection(FilterTables(allTables, options).ToList());
    }

    private static void AssignColumns(TableDefinition table, IEnumerable<dynamic> dynamicColumns)
    {
        IEnumerable<ColumnDefinition> columns = dynamicColumns
            .Where(c => table.Name.Equals((string)c.Table) && table.Schema.Equals((string)c.Schema))
            .Select(c =>
            {
                (Type ClrType, DbType DbType) mappings = TypeMappings.GetMappings(c.DbDataType);
                return new ColumnDefinition(c.Name)
                {
                    DatabaseType = c.DbDataType,
                    ClrType = mappings.ClrType,
                    MaxLength = c.MaxLength is null ? 0 : (int)c.MaxLength,
                    DbType = mappings.DbType,
                    IsNullable = c.IsNullable,
                    IsIdentity = c.IsIdentity,
                    IsAutoGenerated = c.DbDataType == "rowversion" || c.DbDataType == "timestamp",
                };
            });

        foreach (ColumnDefinition column in columns)
            table.Columns.Add(column);
    }

    private static void AssignReferences(TableDefinition table, IEnumerable<dynamic> references)
    {
        IEnumerable<dynamic> tableReferences = references.Where(r => table.Name.Equals((string)r.ReferencingTable)
            && table.Schema.Equals((string)r.ReferencingSchema));

        foreach (dynamic tableReference in tableReferences)
        {
            string columnName = (string)tableReference.ReferencingColumn;
            ColumnDefinition column = table.Columns
                .Single(cd => cd.Name.Equals(columnName, StringComparison.Ordinal));
            if (column.ForeignKey is not null)
                throw new InvalidOperationException();
            column.ForeignKey = new ForeignKeyDefinition((string)tableReference.ReferencedSchema,
                (string)tableReference.ReferencedTable, (string)tableReference.ReferencedColumn);
        }
    }

    public override string GetFullTableName(string schema, string table)
    {
        return $"[{schema}].[{table}]";
    }
}

internal static class TypeMappings
{
    internal static (Type ClrType, DbType DbType) GetMappings(string dbType)
    {
        return dbType switch
        {
            "bigint" => (typeof(long), DbType.Int64),
            "binary" => (typeof(byte[]), DbType.Binary),
            "bit" => (typeof(bool), DbType.Boolean),
            "char" => (typeof(string), DbType.AnsiStringFixedLength),
            "date" => (typeof(DateTime), DbType.Date),
            "datetime" => (typeof(DateTime), DbType.DateTime),
            "datetime2" => (typeof(DateTime), DbType.DateTime2),
            "datetimeoffset" => (typeof(DateTimeOffset), DbType.DateTimeOffset),
            "decimal" => (typeof(decimal), DbType.Decimal),
            "float" => (typeof(double), DbType.Double),
            "image" => (typeof(byte[]), DbType.Binary),
            "int" => (typeof(int), DbType.Int32),
            "money" => (typeof(decimal), DbType.Decimal),
            "nchar" => (typeof(string), DbType.StringFixedLength),
            "ntext" => (typeof(string), DbType.String),
            "numeric" => (typeof(decimal), DbType.Decimal),
            "nvarchar" => (typeof(string), DbType.String),
            "real" => (typeof(float), DbType.Single),
            "rowversion" => (typeof(byte[]), DbType.Binary),
            "smalldatetime" => (typeof(DateTime), DbType.DateTime),
            "smallint" => (typeof(short), DbType.Int16),
            "smallmoney" => (typeof(decimal), DbType.Decimal),
            "sql_variant" => (typeof(object), DbType.Object),
            "text" => (typeof(string), DbType.String),
            "time" => (typeof(TimeSpan), DbType.Time),
            "timestamp" => (typeof(byte[]), DbType.Binary),
            "tinyint" => (typeof(byte), DbType.Byte),
            "uniqueidentifier" => (typeof(Guid), DbType.Guid),
            "varbinary" => (typeof(byte[]), DbType.Binary),
            "varchar" => (typeof(string), DbType.AnsiString),
            "xml" => (typeof(string), DbType.Xml),
            _ => (typeof(object), DbType.Object),
        };
    }
}
