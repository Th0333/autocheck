namespace NotebookCheck.Presentation.Views;

/// <summary>
/// Painel de teste embutido no <see cref="TestActionWindow"/> — roda o teste
/// dentro do próprio modal (sem abrir uma segunda janela). O modal cuida do
/// resultado (dropdown), comentário e salvar/cancelar.
/// </summary>
public interface ITestInlineControl
{
    /// <summary>Para o teste e libera recursos (captura, timers, brilho, etc.).</summary>
    void StopTest();

    /// <summary>Resumo curto do que foi observado, usado no detalhe do resultado.</summary>
    string Summary { get; }
}
