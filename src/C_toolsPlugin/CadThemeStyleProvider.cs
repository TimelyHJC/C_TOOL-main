using System;
using System.Windows;
using System.Windows.Markup;

namespace C_toolsPlugin;

internal static class CadThemeStyleProvider
{
    private static readonly Lazy<Style> s_cadToolbarComboBoxStyle = new(CreateCadToolbarComboBoxStyle);

    internal static Style CadToolbarComboBoxStyle => s_cadToolbarComboBoxStyle.Value;

    private static Style CreateCadToolbarComboBoxStyle()
    {
        try
        {
            var dictionary = (ResourceDictionary)Application.LoadComponent(
                new Uri("/C_toolsShared;component/Themes/CadTheme.xaml", UriKind.Relative));

            if (dictionary["CadToolbarComboBox"] is Style style)
                return style;
        }
        catch
        {
            // Fall back to a local copy so modeless helper windows remain usable
            // even if the shared theme dictionary fails to load.
        }

        return (Style)XamlReader.Parse(
            """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="{x:Type ComboBox}">
              <Setter Property="Background" Value="#262C38" />
              <Setter Property="BorderThickness" Value="0" />
              <Setter Property="Foreground" Value="#E6E8EA" />
              <Setter Property="Padding" Value="0" />
              <Setter Property="MinHeight" Value="0" />
              <Setter Property="VerticalContentAlignment" Value="Center" />
              <Setter Property="SnapsToDevicePixels" Value="True" />
              <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled" />
              <Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto" />
              <Setter Property="ItemContainerStyle">
                <Setter.Value>
                  <Style TargetType="{x:Type ComboBoxItem}">
                    <Setter Property="Foreground" Value="#E6E8EA" />
                    <Setter Property="Background" Value="#262C38" />
                    <Setter Property="Padding" Value="8,6" />
                    <Style.Triggers>
                      <Trigger Property="IsHighlighted" Value="True">
                        <Setter Property="Background" Value="#222731" />
                      </Trigger>
                    </Style.Triggers>
                  </Style>
                </Setter.Value>
              </Setter>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="{x:Type ComboBox}">
                    <Grid SnapsToDevicePixels="True">
                      <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="22" />
                      </Grid.ColumnDefinitions>
                      <Border x:Name="Bd"
                              Grid.ColumnSpan="2"
                              Background="{TemplateBinding Background}"
                              BorderBrush="#21262D"
                              BorderThickness="1"
                              CornerRadius="4" />
                      <ContentPresenter Grid.Column="0"
                                        Margin="8,0,4,0"
                                        HorizontalAlignment="Left"
                                        VerticalAlignment="Center"
                                        IsHitTestVisible="False"
                                        Content="{TemplateBinding SelectionBoxItem}"
                                        ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" />
                      <ToggleButton x:Name="PART_ToggleButton"
                                    Grid.ColumnSpan="2"
                                    Background="Transparent"
                                    BorderThickness="0"
                                    Focusable="False"
                                    ClickMode="Press"
                                    IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                        <ToggleButton.Template>
                          <ControlTemplate TargetType="{x:Type ToggleButton}">
                            <Border Background="Transparent" />
                          </ControlTemplate>
                        </ToggleButton.Template>
                      </ToggleButton>
                      <Path Grid.Column="1"
                            Width="8"
                            Height="5"
                            Margin="0,0,8,0"
                            HorizontalAlignment="Center"
                            VerticalAlignment="Center"
                            Data="M 0,0 L 8,0 L 4,5 Z"
                            Fill="#8B949E"
                            IsHitTestVisible="False" />
                      <Popup x:Name="PART_Popup"
                             AllowsTransparency="True"
                             Focusable="False"
                             IsOpen="{TemplateBinding IsDropDownOpen}"
                             Placement="Bottom"
                             PlacementTarget="{Binding ElementName=Bd}"
                             PopupAnimation="Slide">
                        <Border MinWidth="{Binding ActualWidth, ElementName=Bd}"
                                MaxHeight="{TemplateBinding MaxDropDownHeight}"
                                Background="#262C38"
                                BorderBrush="#21262D"
                                BorderThickness="1"
                                SnapsToDevicePixels="True">
                          <ScrollViewer Margin="0" SnapsToDevicePixels="True">
                            <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained" />
                          </ScrollViewer>
                        </Border>
                      </Popup>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsKeyboardFocusWithin" Value="True">
                        <Setter TargetName="Bd" Property="BorderBrush" Value="#58A6FF" />
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """);
    }
}
