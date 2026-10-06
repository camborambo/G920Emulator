from pathlib import Path

p = Path(r"g:\Projects\Github\G920Emulator\src\G920Emulator.App\MainWindow.xaml")
text = p.read_text(encoding="utf-8")
input_grid = Path(r"g:\Projects\Github\G920Emulator\.tmp\input_grid.xml").read_text(encoding="utf-8")
ffb_scroll = Path(r"g:\Projects\Github\G920Emulator\.tmp\ffb_scroll.xml").read_text(encoding="utf-8")


def indent(s: str, n: int) -> str:
    pad = " " * n
    return "\n".join(pad + line if line.strip() else line for line in s.splitlines())


header_end = text.index("    <!-- Dependencies")
prefix = text[:header_end]

sb0 = text.index("    <StatusBar ")
sb1 = text.index("    </StatusBar>") + len("    </StatusBar>")
status = text[sb0:sb1]

deps0 = text.index("    <!-- Dependencies")
deps1 = text.index("    <!-- Live strip")
live0 = deps1
live1 = text.index("    <StatusBar ")
deps = text[deps0:deps1].rstrip() + "\n"
live = text[live0:live1].rstrip() + "\n"

input_panel = input_grid.replace("<Grid>", '<Grid x:Name="InputPanel">', 1)
ffb_panel = ffb_scroll.replace(
    "<ScrollViewer",
    '<ScrollViewer x:Name="FfbPanel" Visibility="Collapsed"',
    1,
)

new_mid = """    <!-- Tabs + profile (Bridge Simplified) -->
    <DockPanel DockPanel.Dock="Top" Margin="0,0,0,8" LastChildFill="False">
      <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Center">
        <TextBlock Text="Profile" Opacity="0.7" VerticalAlignment="Center" Margin="0,0,8,0"
                   ToolTip="Input profile — bindings / devices. Force feedback has its own profiles on the Force feedback tab."/>
        <TextBox x:Name="ProfileNameBox" Width="140" VerticalAlignment="Center" Margin="0,0,8,0" ToolTip="Input profile name"/>
        <ComboBox x:Name="SavedProfilesCombo" Width="180" VerticalAlignment="Center" Margin="0,0,8,0"
                  SelectionChanged="SavedProfilesCombo_SelectionChanged"/>
        <Button Style="{StaticResource AppButton}" Content="Save" Click="SaveButton_Click" ToolTip="Save input profile to AppData" Padding="10,5"/>
        <Button Style="{StaticResource AppButton}" Content="Save As…" Click="SaveAsButton_Click" Padding="10,5"/>
        <Button Style="{StaticResource AppButton}" Content="Delete" Click="DeleteProfileButton_Click" Padding="10,5"/>
        <Button Style="{StaticResource AppButton}" Content="Export…" Click="ExportButton_Click" Padding="10,5"/>
        <Button Style="{StaticResource AppButton}" Content="Import…" Click="ImportButton_Click" Margin="0" Padding="10,5"/>
      </StackPanel>
      <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <RadioButton x:Name="InputTabRadio" Style="{StaticResource MainTabRadio}" Content="Input"
                     GroupName="MainTabs" IsChecked="True" Checked="MainTab_Checked"/>
        <RadioButton x:Name="FfbTabRadio" Style="{StaticResource MainTabRadio}" Content="Force feedback"
                     GroupName="MainTabs" Checked="MainTab_Checked"/>
      </StackPanel>
    </DockPanel>

"""

body = (
    prefix
    + new_mid
    + deps
    + "\n"
    + live
    + "\n"
    + status
    + """

    <Grid>
"""
    + indent(input_panel, 6)
    + "\n\n"
    + indent(ffb_panel, 6)
    + """
    </Grid>
  </DockPanel>
</Window>
"""
)

p.write_text(body, encoding="utf-8")
assert "x:Name=\"InputPanel\"" in body
assert "x:Name=\"FfbPanel\"" in body
assert "MainTab_Checked" in body
assert "<TabControl" not in body
assert "ProfileNameBox" in body
assert "ButtonsText" in body
print("Wrote", p, "lines", body.count("\n"))
