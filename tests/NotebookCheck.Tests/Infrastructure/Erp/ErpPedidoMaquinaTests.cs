using NotebookCheck.Infrastructure.Erp;

namespace NotebookCheck.Tests.Infrastructure.Erp;

/// <summary>
/// Quais máquinas do pedido de compra o cadastro aceita. A máquina nasce no
/// pedido em "aguardando recebimento" — o ERP só marca pode_check_entrada
/// depois que alguém confirma que a mercadoria chegou. Enquanto o app filtrava
/// só por pode_check_entrada, o pedido recém-criado aparecia sem nenhuma
/// máquina e o POST terminava em "o pedido já recebeu todas as máquinas
/// previstas", sem nada mudar no ERP.
/// </summary>
public class ErpPedidoMaquinaTests
{
    private static ErpPedidoMaquina Maquina(
        string etapa, bool podeCheckEntrada = false, string? serial = null, string? ntb = null) =>
        new()
        {
            AssetId = "11111111-1111-1111-1111-111111111111",
            Ntb = ntb,
            EtapaKanban = etapa,
            SerialNumber = serial,
            PodeCheckEntrada = podeCheckEntrada,
        };

    [Fact]
    public void Maquina_em_branco_aguardando_recebimento_pode_ser_cadastrada()
    {
        var m = Maquina("aguardando_recebimento");
        m.AguardandoRecebimento.Should().BeTrue();
        m.PodeCadastrar.Should().BeTrue();
    }

    [Fact]
    public void Maquina_liberada_no_check_de_entrada_pode_ser_cadastrada()
    {
        var m = Maquina("check_entrada", podeCheckEntrada: true);
        m.AguardandoRecebimento.Should().BeFalse();
        m.PodeCadastrar.Should().BeTrue();
    }

    [Fact]
    public void Maquina_ja_preenchida_nao_pode_ser_cadastrada()
    {
        // preenchida = saiu do "em branco": o ERP devolve pode_check_entrada false
        var m = Maquina("em_andamento", serial: "PF1ABCDE");
        m.AguardandoRecebimento.Should().BeFalse();
        m.PodeCadastrar.Should().BeFalse();
    }

    [Fact]
    public void Aguardando_recebimento_com_serial_ja_nao_esta_em_branco()
    {
        Maquina("aguardando_recebimento", serial: "PF1ABCDE").AguardandoRecebimento.Should().BeFalse();
    }

    [Fact]
    public void Display_do_cadastro_marca_a_que_ainda_precisa_ser_confirmada()
    {
        Maquina("aguardando_recebimento", ntb: "11801").DisplayCadastro
            .Should().Be("NTB11801 · aguardando recebimento");
    }

    [Fact]
    public void Display_do_cadastro_sem_marca_quando_ja_esta_no_check_de_entrada()
    {
        Maquina("check_entrada", podeCheckEntrada: true, ntb: "11801").DisplayCadastro
            .Should().Be("NTB11801");
    }

    [Fact]
    public void Display_nao_duplica_o_prefixo_quando_o_erp_ja_manda_com_NTB()
    {
        // desde 20260807170000 o ERP devolve "NTB11834", não "11834" — a lista
        // do pedido mostrava "NTB NTB11834"
        var m = Maquina("check_entrada", podeCheckEntrada: true, ntb: "NTB11834");
        m.Modelo = "Latitude 5450";
        m.Display.Should().Be("NTB11834 · Latitude 5450");
    }

    [Fact]
    public void Display_avisa_config_errada_enquanto_o_alerta_nao_for_tratado()
    {
        var m = Maquina("aguardando_tecnico", ntb: "NTB11834", serial: "PF1ABCDE");
        m.ConfigDivergente = true;
        m.Display.Should().Be("NTB11834 · ⚠ config errada");
    }
}
