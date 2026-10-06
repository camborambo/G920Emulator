using System.Windows;

namespace G920Emulator.App;

public partial class ProfileNameDialog : Window
{
    public string ProfileName { get; private set; } = "";

    public ProfileNameDialog(string suggested)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        NameBox.Text = suggested;
        NameBox.SelectAll();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a profile name.", "Profile name");
            return;
        }

        ProfileName = name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
