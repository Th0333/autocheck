using System.Collections.Generic;
using System.Collections.ObjectModel;
using NotebookCheck.Domain.Enums;

namespace NotebookCheck.Domain.Models;

/// <summary>
/// Mapeamento estático que associa cada <see cref="ComponentId"/> ao identificador textual
/// em snake_case usado no dicionário <c>tests</c> do payload da API e ao requisito do teste
/// correspondente (conforme tabela "Mapeamento Requisito → Componente" do design).
/// </summary>
/// <remarks>
/// <para>
/// Este mapa é referenciado pelo <c>PayloadBuilder</c> para gerar as chaves de
/// <c>payload.tests</c> e pelo <c>RetestController</c> para escolher qual teste do
/// <c>ITestEngine</c> executar para cada componente selecionado.
/// </para>
/// <para>
/// A propriedade <c>Requirement</c> é uma referência ao identificador do requisito
/// (no formato "Req.&lt;N&gt;") e existe para fins de rastreabilidade.
/// </para>
/// </remarks>
public static class ComponentTestMap
{
    /// <summary>
    /// Entrada imutável associando um <see cref="ComponentId"/> à sua chave textual
    /// em snake_case (<see cref="TestKey"/>) e ao requisito do teste correspondente
    /// (<see cref="Requirement"/>).
    /// </summary>
    public readonly record struct Entry(string TestKey, string Requirement);

    private static readonly IReadOnlyDictionary<ComponentId, Entry> _map =
        new ReadOnlyDictionary<ComponentId, Entry>(new Dictionary<ComponentId, Entry>
        {
            [ComponentId.Tela]          = new Entry("tela",          "Req.7"),
            [ComponentId.Teclado]       = new Entry("teclado",       "Req.14"),
            [ComponentId.Touchpad]      = new Entry("touchpad",      "Req.15"),
            [ComponentId.Webcam]        = new Entry("webcam",        "Req.8"),
            [ComponentId.Bateria]       = new Entry("bateria",       "Req.6"),
            [ComponentId.Armazenamento] = new Entry("armazenamento", "Req.5"),
            [ComponentId.Ram]           = new Entry("ram",           "Req.4"),
            [ComponentId.Audio]         = new Entry("audio",         "Req.10"),
            [ComponentId.Microfone]     = new Entry("microfone",     "Req.10"),
            [ComponentId.Wifi]          = new Entry("wifi",          "Req.9"),
            [ComponentId.Bluetooth]     = new Entry("bluetooth",     "Req.9"),
            [ComponentId.Usb]           = new Entry("usb",           "Req.11"),
            [ComponentId.Hdmi]          = new Entry("hdmi",          "Req.12"),
            [ComponentId.Carregador]    = new Entry("carregador",    "Req.13"),
        });

    /// <summary>
    /// Mapeamento imutável de <see cref="ComponentId"/> para par
    /// (<see cref="Entry.TestKey"/>, <see cref="Entry.Requirement"/>).
    /// </summary>
    public static IReadOnlyDictionary<ComponentId, Entry> Map => _map;

    /// <summary>
    /// Retorna a entrada associada ao <paramref name="component"/> informado.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// Se o <paramref name="component"/> não estiver mapeado. Como o mapa cobre todos os
    /// valores do enum <see cref="ComponentId"/>, esse erro indica adição não acompanhada
    /// no enum e neste mapa.
    /// </exception>
    public static Entry Get(ComponentId component)
    {
        if (!_map.TryGetValue(component, out var entry))
        {
            throw new KeyNotFoundException(
                $"ComponentId '{component}' não possui mapeamento em ComponentTestMap.");
        }
        return entry;
    }

    /// <summary>
    /// Retorna a chave textual em snake_case usada no payload da API para
    /// o <paramref name="component"/> informado.
    /// </summary>
    public static string GetTestKey(ComponentId component) => Get(component).TestKey;

    /// <summary>
    /// Retorna o identificador do requisito (formato "Req.&lt;N&gt;") associado ao
    /// teste do <paramref name="component"/> informado.
    /// </summary>
    public static string GetRequirement(ComponentId component) => Get(component).Requirement;

    /// <summary>
    /// Tenta resolver o <see cref="ComponentId"/> a partir da chave textual em snake_case.
    /// </summary>
    /// <param name="testKey">Chave em snake_case (ex.: "tela", "carregador").</param>
    /// <param name="component">Componente correspondente, quando encontrado.</param>
    /// <returns><c>true</c> se um componente foi encontrado para a chave; caso contrário, <c>false</c>.</returns>
    public static bool TryGetComponent(string testKey, out ComponentId component)
    {
        if (!string.IsNullOrEmpty(testKey))
        {
            foreach (var kvp in _map)
            {
                if (string.Equals(kvp.Value.TestKey, testKey, System.StringComparison.Ordinal))
                {
                    component = kvp.Key;
                    return true;
                }
            }
        }

        component = default;
        return false;
    }
}
