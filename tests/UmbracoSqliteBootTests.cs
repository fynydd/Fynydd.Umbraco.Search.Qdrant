using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core;
using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.Testing;

namespace Umbraco.Search.Qdrant.Tests;

[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest, Boot = true)]
public sealed class UmbracoSqliteBootTests : UmbracoIntegrationTest
{
    public UmbracoSqliteBootTests()
    {
        InMemoryConfiguration["Tests:Database:DatabaseType"] = "SQLite";
        InMemoryConfiguration["Tests:Database:PrepareThreadCount"] = "1";
        InMemoryConfiguration["Tests:Database:SchemaDatabaseCount"] = "1";
        InMemoryConfiguration["Tests:Database:EmptyDatabasesCount"] = "1";
    }

    [Test]
    public async Task CoversUmbracoSqliteIntegration()
    {
        NUnit.Framework.Assert.Multiple(() =>
        {
            NUnit.Framework.Assert.That(ScopeProvider, Is.Not.Null);
            NUnit.Framework.Assert.That(ScopeAccessor, Is.Not.Null);
            NUnit.Framework.Assert.That(GetRequiredService<IContentTypeService>(), Is.Not.Null);
            NUnit.Framework.Assert.That(GetRequiredService<IContentService>(), Is.Not.Null);
        });

        CanPersistAndReadContentTypeFromSqlite();
        CanPersistAndReadContentFromSqlite();
        CanPublishContentInSqlite();
        CanPersistAndReadMediaFromSqlite();
        await CanPersistAndReadCultureVariantContentFromSqlite();
    }

    private void CanPersistAndReadContentTypeFromSqlite()
    {
        var alias = CreateContentType();

        var saved = GetRequiredService<IContentTypeService>().Get(alias);

        NUnit.Framework.Assert.That(saved, Is.Not.Null);
        NUnit.Framework.Assert.That(saved!.Alias, Is.EqualTo(alias));
        NUnit.Framework.Assert.That(saved.AllowedAsRoot, Is.True);
    }

    private void CanPersistAndReadContentFromSqlite()
    {
        var alias = CreateContentType(includeBody: true);
        var contentService = GetRequiredService<IContentService>();

        var content = contentService.Create("Home", Constants.System.Root, alias);
        content.SetValue("body", "<p>Hello <strong>SQLite</strong></p>");
        contentService.Save(content);
        var saved = contentService.GetById(content.Key);

        NUnit.Framework.Assert.That(saved, Is.Not.Null);
        NUnit.Framework.Assert.That(saved!.Name, Is.EqualTo("Home"));
        NUnit.Framework.Assert.That(saved.ContentType.Alias, Is.EqualTo(alias));
        NUnit.Framework.Assert.That(saved.GetValue<string>("body"), Is.EqualTo("<p>Hello <strong>SQLite</strong></p>"));
    }

    private void CanPublishContentInSqlite()
    {
        var alias = CreateContentType(includeBody: true);
        var contentService = GetRequiredService<IContentService>();
        var content = contentService.Create("Published Home", Constants.System.Root, alias);
        content.SetValue("body", "<p>Published <strong>HTML</strong></p>");
        contentService.Save(content);

        var publishResult = contentService.Publish(content, ["*"]);
        var saved = contentService.GetById(content.Key);

        NUnit.Framework.Assert.That(publishResult.Success, Is.True);
        NUnit.Framework.Assert.That(saved, Is.Not.Null);
        NUnit.Framework.Assert.That(saved!.Published, Is.True);
        NUnit.Framework.Assert.That(saved.GetValue<string>("body", published: true), Is.EqualTo("<p>Published <strong>HTML</strong></p>"));
    }

    private void CanPersistAndReadMediaFromSqlite()
    {
        var mediaService = GetRequiredService<IMediaService>();
        var media = mediaService.CreateMedia("Hero", Constants.System.Root, Constants.Conventions.MediaTypes.Image);

        var saveResult = mediaService.Save(media);
        var saved = mediaService.GetById(media.Key);

        NUnit.Framework.Assert.That(saveResult.Success, Is.True);
        NUnit.Framework.Assert.That(saved, Is.Not.Null);
        NUnit.Framework.Assert.That(saved!.Name, Is.EqualTo("Hero"));
        NUnit.Framework.Assert.That(saved.ContentType.Alias, Is.EqualTo(Constants.Conventions.MediaTypes.Image));
    }

    private async Task CanPersistAndReadCultureVariantContentFromSqlite()
    {
        var language = await GetRequiredService<ILanguageService>().GetDefaultLanguageAsync();
        NUnit.Framework.Assert.That(language, Is.Not.Null);
        var alias = CreateContentType(includeBody: true, variations: ContentVariation.Culture);
        var contentService = GetRequiredService<IContentService>();
        var content = contentService.Create("Home", Constants.System.Root, alias);
        content.SetCultureName("Home Variant", language!.IsoCode);
        content.SetValue("body", "<p>Variant <strong>HTML</strong></p>", language.IsoCode);

        contentService.Save(content);

        var saved = contentService.GetById(content.Key);

        NUnit.Framework.Assert.That(saved, Is.Not.Null);
        NUnit.Framework.Assert.That(saved!.GetCultureName(language.IsoCode), Is.EqualTo("Home Variant"));
        NUnit.Framework.Assert.That(saved.GetValue<string>("body", language.IsoCode), Is.EqualTo("<p>Variant <strong>HTML</strong></p>"));
    }

    private string CreateContentType(bool includeBody = false, ContentVariation variations = ContentVariation.Nothing)
    {
        var contentTypeService = GetRequiredService<IContentTypeService>();
        var shortStringHelper = GetRequiredService<IShortStringHelper>();
        var alias = "article" + Guid.NewGuid().ToString("N")[..8];
        var contentType = new ContentType(shortStringHelper, Constants.System.Root)
        {
            Alias = alias,
            Name = "Article",
            AllowedAsRoot = true,
            Variations = variations
        };

        if (includeBody)
        {
#pragma warning disable CS0618
            var dataType = GetRequiredService<IDataTypeService>().GetDataType(Constants.DataTypes.RichtextEditor);
#pragma warning restore CS0618
            NUnit.Framework.Assert.That(dataType, Is.Not.Null);
            var body = new PropertyType(shortStringHelper, dataType!)
            {
                Alias = "body",
                Name = "Body",
                Variations = variations
            };

            contentType.AddPropertyType(body);
        }

#pragma warning disable CS0618
        contentTypeService.Save(contentType);
#pragma warning restore CS0618

        return alias;
    }

    private new T GetRequiredService<T>()
        where T : notnull =>
        Services.GetRequiredService<T>();
}
