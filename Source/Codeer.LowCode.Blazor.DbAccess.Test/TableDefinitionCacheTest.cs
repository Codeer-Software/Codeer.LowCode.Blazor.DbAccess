using Codeer.LowCode.Blazor.SystemSettings;

namespace Codeer.LowCode.Blazor.DbAccess.Test
{
    /// <summary>表定義のキャッシュはテナントごと。テナント名を渡さなければ全部 1 つを共有する。</summary>
    [NonParallelizable]
    public class TableDefinitionCacheTest
    {
        static DataSource[] DataSources(string connectionString)
            => new[] { new DataSource { Name = "Main", DataSourceType = DataSourceType.SQLite, ConnectionString = connectionString } };

        [Test]
        public void テナント名なしは全部同じキャッシュ()
        {
            using var a = new DbAccessor(DataSources("Data Source=a.db"));
            using var b = new DbAccessor(DataSources("Data Source=b.db"));
            Assert.That(a.DbTableDefinitionCache, Is.SameAs(b.DbTableDefinitionCache));
        }

        [Test]
        public void 同じテナントは同じキャッシュ_違うテナントは別のキャッシュ()
        {
            using var a1 = new DbAccessor(DataSources("Data Source=a.db"), "TenantA");
            using var a2 = new DbAccessor(DataSources("Data Source=a.db"), "TenantA");
            using var b = new DbAccessor(DataSources("Data Source=b.db"), "TenantB");
            using var none = new DbAccessor(DataSources("Data Source=a.db"));
            Assert.That(a1.DbTableDefinitionCache, Is.SameAs(a2.DbTableDefinitionCache));
            Assert.That(a1.DbTableDefinitionCache, Is.Not.SameAs(b.DbTableDefinitionCache));
            Assert.That(a1.DbTableDefinitionCache, Is.Not.SameAs(none.DbTableDefinitionCache));
        }

        [Test]
        public void テナントを指定したClearはそのテナントだけ作り直す()
        {
            using var a = new DbAccessor(DataSources("Data Source=a.db"), "TenantA");
            using var b = new DbAccessor(DataSources("Data Source=b.db"), "TenantB");
            var beforeA = a.DbTableDefinitionCache;
            var beforeB = b.DbTableDefinitionCache;
            DbAccessor.ClearTableDefinitionCache("TenantA");
            Assert.That(a.DbTableDefinitionCache, Is.Not.SameAs(beforeA));
            Assert.That(b.DbTableDefinitionCache, Is.SameAs(beforeB));
        }

        [Test]
        public void Clearは全部のテナントを作り直す()
        {
            using var a = new DbAccessor(DataSources("Data Source=a.db"), "TenantA");
            using var none = new DbAccessor(DataSources("Data Source=a.db"));
            var beforeA = a.DbTableDefinitionCache;
            var beforeNone = none.DbTableDefinitionCache;
            DbAccessor.ClearTableDefinitionCache();
            Assert.That(a.DbTableDefinitionCache, Is.Not.SameAs(beforeA));
            Assert.That(none.DbTableDefinitionCache, Is.Not.SameAs(beforeNone));
        }
    }
}
