namespace Gitruvian.Core.Tests;

/// <summary>Segurança e validação de diffs.</summary>
public sealed class SafetyAndDiffsTests
{
    private const string Good =
        "diff --git a/src/a.cs b/src/a.cs\n--- a/src/a.cs\n+++ b/src/a.cs\n@@ -1,2 +1,2 @@\n x = 1\n-y = 2\n+y = 3\n";

    [Theory]
    [InlineData(".env")]
    [InlineData("config/.env.production")]
    [InlineData("id_rsa")]
    [InlineData("certs/server.pem")]
    [InlineData("app.key")]
    [InlineData("my_secrets.yaml")]
    [InlineData(".ssh/config")]
    [InlineData("terraform.tfstate")]
    [InlineData(".npmrc")]
    public void SensitivePaths(string path) => Assert.True(Safety.IsSensitivePath(path));

    [Theory]
    [InlineData(".env.example")]
    [InlineData("src/app.cs")]
    [InlineData("README.md")]
    [InlineData("keyboard.cs")]
    public void NonSensitivePaths(string path) => Assert.False(Safety.IsSensitivePath(path));

    [Theory]
    [InlineData("../x.cs")]
    [InlineData("a/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x")]
    [InlineData(".git/config")]
    [InlineData(".gitruvian/gitruvian.db")]
    [InlineData("a\\b.cs")]
    [InlineData("")]
    [InlineData("x\0.cs")]
    [InlineData(".env")]
    public void UnsafePathsRejected(string path) =>
        Assert.Throws<UnsafePathException>(() => Safety.CheckRepoRelativePath(path));

    [Fact]
    public void SafePathNormalized() => Assert.Equal("src/a.cs", Safety.CheckRepoRelativePath("./src/a.cs"));

    [Fact]
    public void RedactKnownSecretShapes()
    {
        var text =
            "ANTHROPIC_API_KEY=sk-ant-api03-abcdefghijklmnop\n" +
            "aws = AKIAABCDEFGHIJKLMNOP\n" +
            "password: \"hunter2hunter2\"\n" +
            "-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----\n" +
            "normal_code = compute(1, 2)\n";
        var output = Safety.Redact(text);
        foreach (var secret in new[] { "sk-ant-api03", "AKIAABCDEFGHIJKLMNOP", "hunter2hunter2", "MIIabc" })
            Assert.DoesNotContain(secret, output);
        Assert.Contains("normal_code = compute(1, 2)", output);
    }

    [Fact]
    public void CleanInstruction()
    {
        Assert.Equal("corrigir bug", Safety.CleanInstruction("  corrigir\x07 bug  "));
        Assert.Throws<ArgumentException>(() => Safety.CleanInstruction("  "));
        Assert.Throws<ArgumentException>(() => Safety.CleanInstruction(new string('x', 5000)));
    }

    [Fact]
    public void ExtractDiffFromFencedReply()
    {
        var reply = $"COMMIT: Ajusta y\n\nAqui está:\n```diff\n{Good}```\nfim";
        Assert.Equal(Good, Diffs.ExtractDiff(reply));
        Assert.Equal("Ajusta y", Diffs.ExtractCommitMessage(reply));
    }

    [Fact]
    public void ExtractDiffFromRawText() => Assert.StartsWith("diff --git", Diffs.ExtractDiff("texto\n" + Good));

    [Fact]
    public void ExtractDiffMissing() =>
        Assert.Throws<DiffValidationException>(() => Diffs.ExtractDiff("não sei fazer isso"));

    [Fact]
    public void ValidateGoodDiffCounts()
    {
        var parsed = Diffs.Validate(Good);
        Assert.Equal(["src/a.cs"], parsed.Files);
        Assert.Equal(1, parsed.Additions);
        Assert.Equal(1, parsed.Deletions);
    }

    [Fact]
    public void ValidateNewFile()
    {
        const string diff = "diff --git a/n.cs b/n.cs\nnew file mode 100644\n--- /dev/null\n+++ b/n.cs\n@@ -0,0 +1 @@\n+x = 1\n";
        Assert.Equal(["n.cs"], Diffs.Validate(diff).Files);
    }

    public static TheoryData<string> UnsafeOrMalformed => new()
    {
        Good.Replace("src/a.cs", "../outside.cs"),
        Good.Replace("src/a.cs", ".git/hooks/pre-commit"),
        Good.Replace("src/a.cs", ".env"),
        Good.Replace("src/a.cs", "/etc/passwd"),
        "diff --git a/x b/x\nGIT binary patch\nliteral 1\n",
        "diff --git a/l b/l\nnew file mode 120000\n--- /dev/null\n+++ b/l\n@@ -0,0 +1 @@\n+/etc/passwd\n",
        "--- a/x.cs\n+++ b/x.cs\n",
        "",
        "@@ -1 +1 @@\n-a\n+b\n",
    };

    [Theory]
    [MemberData(nameof(UnsafeOrMalformed))]
    public void ValidateRejectsUnsafeOrMalformed(string diff) =>
        Assert.Throws<DiffValidationException>(() => Diffs.Validate(diff));

    [Fact]
    public void ValidateRejectsHugeDiff() =>
        Assert.Throws<DiffValidationException>(() => Diffs.Validate(Good + "+" + new string('x', 500_000) + "\n"));
}
