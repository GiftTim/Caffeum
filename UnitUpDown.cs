using System.Globalization;
using System.Text.RegularExpressions;

namespace CoffeeViewer;

/// <summary>칸 안에 단위(pt, %)가 같이 보이는 숫자 입력칸: "10.0      pt ⇅". 값을 직접 써도 되고 화살표로 올리고 내려도 된다.</summary>
internal sealed class UnitUpDown : NumericUpDown
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Unit { get; set; } = "";

    /// <summary>단위를 바꾼 뒤 칸 글자를 다시 쓴다.</summary>
    public void RefreshText() => UpdateEditText();

    protected override void UpdateEditText()
    {
        string num = Value.ToString("F" + DecimalPlaces, CultureInfo.CurrentCulture);
        Text = Unit.Length == 0 ? num : num + new string(' ', Math.Max(2, 9 - num.Length)) + Unit; // 단위는 칸 오른쪽 쪽에
    }

    protected override void ValidateEditText()
    {
        try
        {
            var m = Regex.Match(Text, @"-?\d+([.,]\d+)?");
            if (m.Success && decimal.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                Value = Math.Clamp(v, Minimum, Maximum);
        }
        catch { }
        finally { UserEdit = false; UpdateEditText(); }
    }
}
