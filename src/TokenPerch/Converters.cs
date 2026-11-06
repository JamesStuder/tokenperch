using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TokenPerch;

public static class Converters
{
    public static readonly IValueConverter BoldIf = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.Bold : FontWeight.Normal);
    public static readonly IValueConverter FullIf = new FuncValueConverter<bool, double>(b => b ? 1.0 : 0.6);
}
