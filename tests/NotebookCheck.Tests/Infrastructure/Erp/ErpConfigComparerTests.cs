using NotebookCheck.Infrastructure.Erp;

namespace NotebookCheck.Tests.Infrastructure.Erp;

/// <summary>
/// Compara a config acordada no pedido de compra com a coletada da máquina.
/// A mesma regra vale para o cadastro (check de entrada) e o teste completo.
/// </summary>
public class ErpConfigComparerTests
{
    private static ErpConfigAcordada Acordada(string? cpu = null, int? ram = null, int? storage = null) =>
        new() { Processador = cpu, RamGb = ram, StorageGb = storage };

    private static ErpEspecificacoes Specs(string? cpu = null, int? ram = null, int? storage = null) =>
        new() { Processador = cpu, RamGb = ram, StorageGb = storage };

    [Fact]
    public void Sem_config_acordada_nao_gera_divergencia()
    {
        ErpConfigComparer.Divergencias(null, Specs(ram: 8)).Should().BeEmpty();
    }

    [Fact]
    public void Sem_specs_coletadas_nao_gera_divergencia()
    {
        ErpConfigComparer.Divergencias(Acordada(ram: 16), null).Should().BeEmpty();
    }

    [Fact]
    public void Ram_diferente_do_acordado_e_divergencia()
    {
        var divs = ErpConfigComparer.Divergencias(Acordada(ram: 16), Specs(ram: 8));
        divs.Should().ContainSingle().Which.Should().Be("RAM: acordado 16 GB, encontrado 8 GB");
    }

    [Fact]
    public void Ram_igual_nao_e_divergencia()
    {
        ErpConfigComparer.Divergencias(Acordada(ram: 16), Specs(ram: 16)).Should().BeEmpty();
    }

    [Theory]
    // disco de 512 GB reporta ~477 GiB reais: dentro da tolerância de 88%–130%
    [InlineData(512, 477)]
    [InlineData(256, 238)]
    [InlineData(1000, 931)]
    public void Armazenamento_dentro_da_tolerancia_nao_e_divergencia(int acordado, int encontrado)
    {
        ErpConfigComparer.Divergencias(Acordada(storage: acordado), Specs(storage: encontrado))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(512, 256)] // metade: trocaram o disco
    [InlineData(256, 1000)] // muito maior que o acordado
    public void Armazenamento_fora_da_tolerancia_e_divergencia(int acordado, int encontrado)
    {
        ErpConfigComparer.Divergencias(Acordada(storage: acordado), Specs(storage: encontrado))
            .Should().ContainSingle().Which.Should().StartWith("Armazenamento: acordado");
    }

    [Theory]
    // o acordado costuma vir abreviado; o WMI devolve o nome completo
    [InlineData("i5-8350U", "Intel(R) Core(TM) i5-8350U CPU @ 1.70GHz")]
    [InlineData("i5 8350u", "Intel(R) Core(TM) i5-8350U CPU @ 1.70GHz")]
    [InlineData("Ryzen 5 3500U", "AMD Ryzen 5 3500U with Radeon Vega Mobile Gfx")]
    public void Processador_equivalente_nao_e_divergencia(string acordado, string encontrado)
    {
        ErpConfigComparer.Divergencias(Acordada(cpu: acordado), Specs(cpu: encontrado))
            .Should().BeEmpty();
    }

    [Fact]
    public void Processador_diferente_e_divergencia()
    {
        var divs = ErpConfigComparer.Divergencias(
            Acordada(cpu: "i7-8650U"), Specs(cpu: "Intel(R) Core(TM) i5-8350U CPU @ 1.70GHz"));
        divs.Should().ContainSingle().Which.Should().StartWith("Processador: acordado i7-8650U");
    }

    [Fact]
    public void Acumula_todas_as_divergencias()
    {
        var divs = ErpConfigComparer.Divergencias(
            Acordada(cpu: "i7-8650U", ram: 16, storage: 512),
            Specs(cpu: "i5-8350U", ram: 8, storage: 128));
        divs.Should().HaveCount(3);
    }

    [Fact]
    public void Campo_ausente_no_acordado_nao_compara()
    {
        // pedido só fixou a RAM: processador e disco ficam livres
        ErpConfigComparer.Divergencias(Acordada(ram: 8), Specs(cpu: "i3-6100U", ram: 8, storage: 128))
            .Should().BeEmpty();
    }

    [Fact]
    public void Describe_monta_o_resumo_da_config_acordada()
    {
        ErpConfigComparer.Describe(Acordada("i5-8350U", 16, 256))
            .Should().Be("i5-8350U · 16 GB RAM · 256 GB armazenamento");
    }

    [Fact]
    public void Describe_sem_config_devolve_vazio()
    {
        ErpConfigComparer.Describe(null).Should().BeEmpty();
    }
}
