// wiki: testing#unit-tests
using SharpAudio.Api.Services.Pnc;

namespace SharpAudio.Api.Tests;

public sealed class PncModelCatalogTests
{
    [Fact]
    public void Default_catalog_contains_the_english_pnc_model()
    {
        var catalog = new PncModelCatalog();

        var models = catalog.GetSupportedModels();

        Assert.Single(models);
        Assert.Equal("pnc-en", models.First().Name);
        Assert.Equal("pnc", models.First().Engine);
    }

    [Fact]
    public void Default_model_declares_onnx_tokenizer_and_config_assets()
    {
        var catalog = new PncModelCatalog();

        Assert.True(catalog.TryGetModel("pnc-en", out var model));
        Assert.Equal("punct_cap_seg_en.onnx", model!.OnnxPath);
        Assert.Equal("spe_32k_lc_en.model", model.TokenizerPath);
        Assert.Equal("config.yaml", model.ConfigPath);
        Assert.Contains("en", model.SupportedLanguages);
        Assert.Contains(model.Assets, a => a.RelativePath == model.OnnxPath);
        Assert.Contains(model.Assets, a => a.RelativePath == model.TokenizerPath);
        Assert.Contains(model.Assets, a => a.RelativePath == model.ConfigPath);
    }

    [Fact]
    public void Empty_model_name_returns_the_default_model()
    {
        var catalog = new PncModelCatalog();

        Assert.True(catalog.TryGetModel("", out var model));
        Assert.Equal("pnc-en", model!.Name);
    }

    [Fact]
    public void Unknown_model_name_is_not_found()
    {
        var catalog = new PncModelCatalog();

        Assert.False(catalog.TryGetModel("does-not-exist", out var model));
        Assert.Null(model);
    }

    [Fact]
    public void Catalog_reads_pnc_models_from_config_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "pnc-catalog-test.json");
        File.WriteAllText(path, """
        {
          "pncModels": [
            {
              "name": "pnc-de",
              "engine": "pnc",
              "onnxPath": "model.onnx",
              "tokenizerPath": "tokenizer.model",
              "configPath": "config.yaml",
              "assets": [{ "relativePath": "model.onnx", "url": "https://example.invalid/model.onnx" }],
              "supportedLanguages": ["de"]
            }
          ]
        }
        """);

        try
        {
            var catalog = new PncModelCatalog(path);

            Assert.True(catalog.TryGetModel("pnc-de", out var model));
            Assert.Equal("de", Assert.Single(model!.SupportedLanguages));
            Assert.Equal("model.onnx", model.OnnxPath);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
