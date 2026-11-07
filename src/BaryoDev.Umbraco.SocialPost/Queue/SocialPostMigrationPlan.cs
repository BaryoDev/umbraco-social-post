using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Infrastructure.Migrations;

namespace BaryoDev.Umbraco.SocialPost.Queue;

public sealed class SocialPostMigrationPlan() : PackageMigrationPlan("BaryoDev.Umbraco.SocialPost")
{
    protected override void DefinePlan()
    {
        To<CreateTables>("2b8e5d1a-7c43-4f60-a9d2-5e1c0b3f8a74");
    }
}

public sealed class CreateTables(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (!TableExists(QueueRow.TableName))
        {
            Create.Table<QueueRow>().Do();
        }

        if (!TableExists(CredentialRow.TableName))
        {
            Create.Table<CredentialRow>().Do();
        }

        return Task.CompletedTask;
    }
}
