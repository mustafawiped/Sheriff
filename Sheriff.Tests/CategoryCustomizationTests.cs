using Sheriff.Core;
using Sheriff.Core.Categorization;
using Xunit;

namespace Sheriff.Tests;

public sealed class CategoryCustomizationTests
{
    [Fact]
    public void RenamingBuiltInCategoryPreservesAutomaticRulesAndAssignments()
    {
        var config = new SheriffConfig();
        var app = new AppEntry { Id = "editor", TargetPathOrAumid = @"C:\Apps\editor.exe" };
        var categorizer = new AppCategorizer([new() { Name = "Editor", Category = Categories.Software, ExeNames = ["editor.exe"] }]);
        CategoryCustomization.RenameCategory(config, Categories.Software, "Geliştirici Araçları");
        Assert.Equal("Geliştirici Araçları", CategoryCustomization.GetDisplayName(config, Categories.Software));
        Assert.Contains(Categories.Software, config.Categories);
        Assert.Equal(Categories.Software, categorizer.Categorize(app, config));
        CategoryCustomization.AssignCategory(config, "editor", Categories.Software);
        Assert.Equal(Categories.Software, config.CategoryOverrides["EDITOR"]);
    }

    [Fact]
    public void AddedCategoryCanBeRenamedWithoutChangingItsIdentityOrAssignments()
    {
        var config = new SheriffConfig();
        var id = CategoryCustomization.AddCategory(config, "  İş  ");
        CategoryCustomization.AssignCategory(config, "app", id);
        CategoryCustomization.RenameCategory(config, id, "Ofis");
        Assert.Equal("İş", id);
        Assert.Contains(id, config.Categories);
        Assert.Contains(id, config.AvailableCategories);
        Assert.Equal(id, config.CategoryOverrides["app"]);
        Assert.Equal("Ofis", CategoryCustomization.GetDisplayName(config, id));
        CategoryCustomization.RenameCategory(config, id, id);
        Assert.False(config.CategoryLabels.ContainsKey(id));
    }

    [Fact]
    public void NewAndRenamedCategoriesRejectCanonicalAndVisibleNameCollisions()
    {
        var config = new SheriffConfig();
        CategoryCustomization.RenameCategory(config, Categories.Software, "Araçlar");
        Assert.Throws<ArgumentException>(() => CategoryCustomization.AddCategory(config, "araçlar"));
        Assert.Throws<ArgumentException>(() => CategoryCustomization.AddCategory(config, " yazılım "));
        Assert.Throws<ArgumentException>(() => CategoryCustomization.RenameCategory(config, Categories.Games, "Araçlar"));
        Assert.Throws<ArgumentException>(() => CategoryCustomization.AddCategory(config, "\n"));
        Assert.Throws<ArgumentException>(() => CategoryCustomization.AddCategory(config, new string('x', 41)));
    }

    [Fact]
    public void ColorsAreNormalizedAndCanBeResetWithoutChangingCategory()
    {
        var config = new SheriffConfig();
        CategoryCustomization.SetColor(config, Categories.Games, " #ab12ef ");
        Assert.Equal("#AB12EF", CategoryCustomization.GetColor(config, Categories.Games));
        Assert.Throws<ArgumentException>(() => CategoryCustomization.SetColor(config, Categories.Games, "red"));
        Assert.Equal("#AB12EF", CategoryCustomization.GetColor(config, Categories.Games));
        CategoryCustomization.SetColor(config, Categories.Games, null);
        Assert.Null(CategoryCustomization.GetColor(config, Categories.Games));
        Assert.Contains(Categories.Games, config.Categories);
    }

    [Fact]
    public void AssignmentRequiresAnEnabledCategoryAndPreservesOtherAssignments()
    {
        var config = new SheriffConfig { AvailableCategories = ["Kapalı"] };
        config.CategoryOverrides["other"] = Categories.Games;
        Assert.Throws<ArgumentException>(() => CategoryCustomization.AssignCategory(config, "app", "Kapalı"));
        Assert.False(config.CategoryOverrides.ContainsKey("app"));
        Assert.Throws<ArgumentException>(() => CategoryCustomization.AssignCategory(config, "app", "Bilinmeyen"));
        Assert.Equal(Categories.Games, config.CategoryOverrides["other"]);
    }
}
