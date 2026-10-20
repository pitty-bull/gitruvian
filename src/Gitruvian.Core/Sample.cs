using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gitruvian.Core;

public sealed class SampleException(string message) : InvalidOperationException(message);

/// <summary>
/// Dados de exemplo: um repositório C# fictício ("Loja Exemplo"), claramente marcado e fácil de apagar. Os testes
/// do exemplo não usam pacotes do NuGet (rodam com <c>dotnet run</c>), então funcionam sem internet; precisam só
/// do SDK do .NET instalado.
/// </summary>
public static partial class Sample
{
    public const string MarkerFile = "DADOS_DE_EXEMPLO.md";
    public const string MarkerText = "GITRUVIAN-SAMPLE-REPO";
    public const string TestCommand = "dotnet run --project testes/Loja.Testes";
    public const string Instruction = "[EXEMPLO] Frete grátis deve valer também para pedidos de exatamente R$ 200,00";
    public const string CarrinhoPath = "src/Loja/Carrinho.cs";
    private const string Buggy = "        if (Subtotal() - Desconto() > FreteGratisAPartirDe)";
    private const string Fixed = "        if (Subtotal() - Desconto() >= FreteGratisAPartirDe)";
    private const string TfmToken = "@TFM@";

    public static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>
    {
        [MarkerFile] = """
            # Dados de exemplo do Gitruvian

            GITRUVIAN-SAMPLE-REPO

            Este repositório é FICTÍCIO e foi gerado pelo Gitruvian (menu Exemplo › Criar repositório de exemplo).
            A "Loja Exemplo", seus produtos e preços não existem.

            Para apagar tudo: menu Exemplo › Apagar este repositório de exemplo (ou apague a pasta).

            """,
        ["README.md"] = """
            # Loja Exemplo (dados fictícios)

            Pequeno carrinho de compras em C# usado para demonstrar o ciclo do Gitruvian.
            Há um bug conhecido: o frete grátis não vale para pedidos de exatamente R$ 200,00.

            Rodar os testes (sem pacotes externos): `dotnet run --project testes/Loja.Testes`

            """,
        [".gitignore"] = "bin/\nobj/\n",
        // Isolam o exemplo de configurações de build de pastas acima dele.
        ["Directory.Build.props"] = "<Project>\n</Project>\n",
        ["Directory.Packages.props"] = """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>

            """,
        ["src/Loja/Loja.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- [EXEMPLO] Biblioteca fictícia da Loja Exemplo. -->
              <PropertyGroup>
                <TargetFramework>@TFM@</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>

            """,
        [CarrinhoPath] = """
            // [EXEMPLO] Carrinho de compras da Loja Exemplo (dados fictícios).
            namespace Loja;

            public sealed record Item(string Sku, string Descricao, decimal PrecoUnitario, int Quantidade = 1)
            {
                public decimal Subtotal() => PrecoUnitario * Quantidade;
            }

            public sealed class Carrinho
            {
                public const decimal FretePadrao = 19.90m;
                public const decimal FreteGratisAPartirDe = 200.00m;

                private readonly List<Item> _itens = [];

                public decimal CupomPercentual { get; init; }

                public IReadOnlyList<Item> Itens => _itens;

                public void Adicionar(Item item)
                {
                    if (item.Quantidade <= 0)
                        throw new ArgumentException("a quantidade deve ser positiva");
                    _itens.Add(item);
                }

                public decimal Subtotal() => _itens.Sum(i => i.Subtotal());

                public decimal Desconto() =>
                    Math.Round(Subtotal() * CupomPercentual / 100, 2, MidpointRounding.AwayFromZero);

                public decimal Frete()
                {
                    if (Subtotal() - Desconto() > FreteGratisAPartirDe)
                        return 0.00m;
                    return FretePadrao;
                }

                public decimal Total() => Subtotal() - Desconto() + Frete();
            }

            """,
        ["testes/Loja.Testes/Loja.Testes.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- [EXEMPLO] Testes do carrinho fictício, sem pacotes externos (roda com dotnet run). -->
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>@TFM@</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../../src/Loja/Loja.csproj" />
              </ItemGroup>
            </Project>

            """,
        ["testes/Loja.Testes/Program.cs"] = """
            // [EXEMPLO] Testes do carrinho fictício. Sai com código 1 se algum teste falhar.
            using Loja;

            var falhas = 0;
            var total = 0;

            void Teste(string nome, Action corpo)
            {
                total++;
                try
                {
                    corpo();
                    Console.WriteLine($"ok     {nome}");
                }
                catch (Exception e)
                {
                    falhas++;
                    Console.WriteLine($"FALHA  {nome}: {e.Message}");
                }
            }

            void Igual(decimal esperado, decimal obtido)
            {
                if (esperado != obtido)
                    throw new Exception($"esperado {esperado}, obtido {obtido}");
            }

            Teste("subtotal soma os itens", () =>
            {
                var carrinho = new Carrinho();
                carrinho.Adicionar(new Item("CAN-001", "Caneca de cerâmica", 34.90m, 2));
                carrinho.Adicionar(new Item("CAD-010", "Caderno pautado", 18.50m));
                Igual(88.30m, carrinho.Subtotal());
            });

            Teste("cupom percentual", () =>
            {
                var carrinho = new Carrinho { CupomPercentual = 10 };
                carrinho.Adicionar(new Item("LUM-200", "Luminária de mesa", 129.90m));
                Igual(12.99m, carrinho.Desconto());
            });

            Teste("frete padrão abaixo do mínimo", () =>
            {
                var carrinho = new Carrinho();
                carrinho.Adicionar(new Item("CAN-001", "Caneca de cerâmica", 34.90m));
                Igual(Carrinho.FretePadrao, carrinho.Frete());
            });

            Teste("frete grátis no valor mínimo exato", () =>
            {
                var carrinho = new Carrinho();
                carrinho.Adicionar(new Item("CAD-300", "Cadeira dobrável", 100.00m, 2));
                Igual(0.00m, carrinho.Frete());
            });

            Teste("quantidade inválida", () =>
            {
                try
                {
                    new Carrinho().Adicionar(new Item("CAN-001", "Caneca de cerâmica", 34.90m, 0));
                }
                catch (ArgumentException)
                {
                    return;
                }
                throw new Exception("deveria rejeitar quantidade zero");
            });

            Console.WriteLine(falhas == 0 ? $"PASSOU: {total} testes" : $"FALHOU: {falhas} de {total} testes");
            return falhas == 0 ? 0 : 1;

            """,
    };

    [GeneratedRegex(@"^(\d+)\.")]
    private static partial Regex SdkMajorRe();

    /// <summary>
    /// Framework do exemplo: o do SDK mais novo instalado (net8.0 ou maior). Sem SDK, usa net8.0; o exemplo é
    /// criado mesmo assim e os testes dele avisam que o comando dotnet não foi encontrado.
    /// </summary>
    public static string TargetFramework()
    {
        try
        {
            var result = Processes.Run(["dotnet", "--list-sdks"], null, null, 30,
                new Dictionary<string, string>(TestRunner.Environment));
            var major = result.StdoutText.Split('\n')
                .Select(l => SdkMajorRe().Match(l.Trim()))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value))
                .DefaultIfEmpty(8)
                .Max();
            return $"net{Math.Max(8, major)}.0";
        }
        catch (FileNotFoundException)
        {
            return "net8.0";
        }
    }

    private static void Write(string root, string rel, string content)
    {
        var target = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
    }

    /// <summary>Cria o repositório de exemplo, indexa e registra uma proposta de exemplo.</summary>
    public static (string Repo, Proposal Proposal) Create(string path, Settings? settings = null)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new SampleException($"a pasta já existe e não está vazia: {path}");
        Directory.CreateDirectory(path);
        var tfm = TargetFramework();
        foreach (var (rel, content) in Files)
            Write(path, rel, content.Replace(TfmToken, tfm));

        Git.Run(path, "init", "-q");
        Git.Run(path, "config", "core.autocrlf", "false");
        Git.Run(path, "config", "user.name", "Gitruvian Exemplo");
        Git.Run(path, "config", "user.email", "exemplo@example.invalid");
        Git.Run(path, "config", "commit.gpgsign", "false");
        Git.Run(path, "add", "-A");
        Git.Commit(path, "[EXEMPLO] Commit inicial da Loja Exemplo (dados fictícios)\n");

        // Gera o diff de correção a partir do próprio git, garantindo que ele aplica.
        var original = Files[CarrinhoPath];
        Write(path, CarrinhoPath, original.Replace(Buggy, Fixed));
        var diff = Git.DiffText(path, paths: [CarrinhoPath]);
        Write(path, CarrinhoPath, original);
        var parsed = Diffs.Validate(diff);

        using var app = AppSession.Open(path, create: true, settings: settings);
        app.Store.SetSetting(Workflow.TestCommandKey, TestCommand);
        Indexer.IndexRepo(app.Store, app.Repo);
        var proposal = app.Store.AddProposal(Instruction, "exemplo", "-", parsed.Text, parsed.Files,
            "[EXEMPLO] Corrige frete grátis no valor mínimo exato", isSample: true);
        app.Store.AddEvent("sample", "[EXEMPLO] Repositório e proposta de exemplo criados", proposal.Id,
            new JsonObject { ["files"] = new JsonArray(parsed.Files.Select(f => (JsonNode?)f).ToArray()) },
            isSample: true);
        return (app.Repo, proposal);
    }

    public static bool IsSampleRepo(string path)
    {
        var marker = Path.Combine(path, MarkerFile);
        try
        {
            return File.Exists(marker) && File.ReadAllText(marker).Contains(MarkerText);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Remove(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
            throw new SampleException($"pasta não encontrada: {path}");
        if (!IsSampleRepo(path))
            throw new SampleException($"{path} não tem o marcador {MarkerFile}; por segurança, nada foi apagado");
        Log.Close(); // o log fica dentro da pasta do exemplo
        // O git marca objetos como somente leitura; tira o atributo antes de apagar.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
