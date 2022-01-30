﻿using Datask.Providers.DbManagement;

using Npgsql;

namespace Datask.Providers.PostgreSql
{
    public sealed class PostgreSqlDbManagementProvider : DbManagementProvider<NpgsqlConnection>
    {
        public PostgreSqlDbManagementProvider(NpgsqlConnection connection)
            : base(connection)
        {
        }

        public override Task ExecuteScriptAsync(IAsyncEnumerable<string> scripts)
        {
            throw new NotImplementedException();
        }
    }
}
