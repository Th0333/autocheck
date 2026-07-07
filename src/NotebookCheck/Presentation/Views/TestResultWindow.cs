namespace NotebookCheck.Presentation.Views;

public abstract class TestResultWindow : System.Windows.Window
{
    public bool? Result { get; protected set; }
    public string Message { get; protected set; } = "";

    protected void Approve(string message)
    {
        Result = true;
        Message = message;
        DialogResult = true;
        Close();
    }

    protected void Reject(string message)
    {
        Result = false;
        Message = message;
        DialogResult = false;
        Close();
    }
}
