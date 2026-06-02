using System.Windows;

namespace FileLock.UI;

/// <summary>
/// Small modal dialog used on first run (when no default password exists yet) to capture and
/// confirm the password before processing dropped files. The entered value is exposed via
/// <see cref="Password"/> when the dialog returns <c>true</c>.
/// </summary>
public partial class PasswordPromptWindow : Window
{
    public string? Password { get; private set; }

    public PasswordPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string pw = PasswordInput.Password;
        string confirm = ConfirmInput.Password;

        if (string.IsNullOrEmpty(pw))
        {
            ShowError("Enter a password.");
            PasswordInput.Focus();
            return;
        }
        if (pw != confirm)
        {
            ShowError("The two passwords don't match.");
            ConfirmInput.Focus();
            return;
        }

        Password = pw;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
