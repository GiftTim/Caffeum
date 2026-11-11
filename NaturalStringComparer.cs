namespace CoffeeViewer;

/// <summary>
/// "2.jpg"가 "10.jpg"보다 앞에 오도록(사전순이 아니라 숫자 크기 순으로) 비교한다.
/// 만화 zip 안 파일 이름이 흔히 이런 식(1.jpg, 2.jpg, ... 10.jpg)이라 꼭 필요하다.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a[si..i].TrimStart('0');
                string nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length - nb.Length;
                int numCmp = string.CompareOrdinal(na, nb);
                if (numCmp != 0) return numCmp;
                // 정수부가 같을 때 한쪽만 소수부(".5")가 이어지면 그쪽이 뒤 — "8권, 8.5권, 9권" 순이 되게.
                bool decA = HasDecimalPart(a, i), decB = HasDecimalPart(b, j);
                if (decA != decB) return decA ? 1 : -1;
            }
            else
            {
                int c = a[i].CompareTo(b[j]);
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (a.Length - i) - (b.Length - j);
    }

    private static bool HasDecimalPart(string s, int i) => i + 1 < s.Length && s[i] == '.' && char.IsDigit(s[i + 1]);
}
