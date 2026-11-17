"""docs/themes/themes.json (design tokens) -> src/Foretop.App/Themes/Colors.<Id>.xaml, checked against Colors.Dark.xaml's keys.

    python tools/themes/make_theme_xaml.py            (from the repository root)

Every theme in themes.json is written again; edit the JSON, never the generated XAML. A new theme also needs a line in
Services/ThemeManager.cs (display name -> id)."""
import json, re, sys, os
root = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
themes_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(root, 'docs', 'themes', 'themes.json')
dark = open(os.path.join(root, 'src/Foretop.App/Themes/Colors.Dark.xaml'), encoding='utf-8').read()
keys = re.findall(r'<SolidColorBrush x:Key="(\w+)"', dark)
data = json.load(open(themes_path, encoding='utf-8'))
hexre = re.compile(r'^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$')
section = {'Steel': 8, 'ChartRoom': 2, 'CIC': 0}   # for the first three themes, from before radiusSection was in the schema

def fam(v):
    return v.replace('&', '&amp;').replace('<', '&lt;')

for t in data['themes']:
    tid = t['id']
    assert re.fullmatch(r'[A-Za-z]+', tid), tid
    colors = t['colors']
    missing = [k for k in keys if k not in colors]
    bad = [k for k, v in colors.items() if not hexre.match(v)]
    assert not missing and not bad, (tid, missing, bad)
    lines = ['<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
             '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
             '                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">',
             '',
             f'    <!-- Foretop theme "{t["name"]}": generated from its design tokens (design canvas "Foretop design themes").',
             '         Every key of Colors.Dark.xaml is here; keep them in step. -->']
    lines += [f'    <SolidColorBrush x:Key="{k}" Color="{colors[k].upper()}" />' for k in keys]
    lines += ['',
              '    <!-- Shape, type and density -->',
              f'    <CornerRadius x:Key="RadiusCard">{t["radiusCard"]}</CornerRadius>',
              f'    <CornerRadius x:Key="RadiusSection">{t.get("radiusSection", section.get(tid, t["radiusCard"]))}</CornerRadius>',
              f'    <CornerRadius x:Key="RadiusControl">{t["radiusControl"]}</CornerRadius>',
              f'    <CornerRadius x:Key="RadiusChip">{t["radiusChip"]}</CornerRadius>',
              f'    <CornerRadius x:Key="RadiusSmall">{t["radiusSmall"]}</CornerRadius>',
              f'    <FontFamily x:Key="ThemeFontUi">{fam(t["fontUi"])}</FontFamily>',
              f'    <FontFamily x:Key="ThemeFontHeading">{fam(t["fontHeading"])}</FontFamily>',
              f'    <sys:Double x:Key="RowHeight">{t["rowHeight"]}</sys:Double>',
              f'    <sys:Boolean x:Key="IsDarkTheme">{"True" if t["isDark"] else "False"}</sys:Boolean>',
              '</ResourceDictionary>', '']
    open(os.path.join(root, f'src/Foretop.App/Themes/Colors.{tid}.xaml'), 'w', encoding='utf-8').write('\n'.join(lines))
    print(tid, t['name'], len(keys), 'colours')
