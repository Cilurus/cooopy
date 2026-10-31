using System.Text.RegularExpressions;

namespace Cooopy.Services
{
    /// <summary>
    /// 敏感内容处理。目前只处理居民身份证号。
    ///
    /// 识别不靠「18 位数字」这种粗糙规则，而是四道校验：
    ///   1. 独立成词：前后不接数字或字母（避免从长数字串里截一段出来）；
    ///   2. 省级地区码在 11–82 之间；
    ///   3. 第 7–14 位是合法生日（1900 年至今，含闰年判断）；
    ///   4. 第 18 位符合 ISO 7064:1983 MOD 11-2 校验位。
    /// 随机 18 位数字能同时过关的概率在千分之一量级，误伤订单号的可能性很低。
    ///
    /// 15 位老身份证没有校验位，只能靠生日判断，误报率高，这里刻意不处理。
    /// </summary>
    public static class SensitiveText
    {
        private static readonly Regex IdCardPattern = new(
            @"(?<![0-9A-Za-z])\d{17}[0-9Xx](?![0-9A-Za-z])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex BankCardPattern = new(
            @"(?<![0-9A-Za-z])\d{16,19}(?![0-9A-Za-z])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // ISO 7064:1983 MOD 11-2 的加权因子与校验码表
        private static readonly int[] Weights = { 7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2 };
        private const string CheckCodes = "10X98765432";

        /// <summary>
        /// 把文本中的身份证号中间 8 位（出生日期）替换成 *，保留前 6 位地区码和后 4 位。
        /// 例如 110101199003071234 -> 110101********1234。
        /// 已打码的文本不会再次命中（* 打断了 18 位连续数字），可重复调用。
        /// </summary>
        public static string MaskIdCards(string text, out int count)
        {
            count = 0;
            if (string.IsNullOrEmpty(text) || text.Length < 18) return text;

            var hits = 0;
            var result = IdCardPattern.Replace(text, match =>
            {
                if (!IsValidIdCard(match.Value)) return match.Value;
                hits++;
                return match.Value[..6] + "********" + match.Value[^4..];
            });

            count = hits;
            return result;
        }

        /// <summary>是否为合法的 18 位居民身份证号（含校验位验证）。</summary>
        public static bool IsValidIdCard(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 18) return false;

            // 省级行政区划代码只有 11–82
            if (!int.TryParse(s.AsSpan(0, 2), out var province) || province < 11 || province > 82)
                return false;

            // 出生日期必须真实存在
            if (!int.TryParse(s.AsSpan(6, 4), out var year) ||
                !int.TryParse(s.AsSpan(10, 2), out var month) ||
                !int.TryParse(s.AsSpan(12, 2), out var day))
                return false;
            if (year < 1900 || year > DateTime.Now.Year) return false;
            if (month < 1 || month > 12) return false;
            if (day < 1 || day > DateTime.DaysInMonth(year, month)) return false;

            // 校验位：前 17 位加权求和后对 11 取模
            var sum = 0;
            for (var i = 0; i < 17; i++)
            {
                var c = s[i];
                if (c < '0' || c > '9') return false;
                sum += (c - '0') * Weights[i];
            }

            return CheckCodes[sum % 11] == char.ToUpperInvariant(s[17]);
        }

        /// <summary>生成“已自动打码 …”的提示文案；没有命中时返回 null。</summary>
        public static string DescribeMask(int idCards, int bankCards)
        {
            if (idCards == 0 && bankCards == 0) return null;
            var parts = new List<string>();
            if (idCards > 0) parts.Add($"{idCards} 处身份证号");
            if (bankCards > 0) parts.Add($"{bankCards} 处银行卡号");
            return "已自动打码 " + string.Join("、", parts);
        }

        /// <summary>
        /// 同时处理身份证与银行卡，返回打码后的文本和各自命中数。
        /// 顺序很重要：先打身份证，打码后的 * 会让银行卡规则不再命中，
        /// 避免同一串数字被处理两次。
        /// </summary>
        public static string MaskAll(string text, out int idCards, out int bankCards)
        {
            var result = MaskIdCards(text, out idCards);
            result = MaskBankCards(result, out bankCards);
            return result;
        }

        /// <summary>银行卡号打码：保留前 4 位（卡组织/BIN）和后 4 位，中间全部替换成 *。</summary>
        public static string MaskBankCards(string text, out int count)
        {
            count = 0;
            if (string.IsNullOrEmpty(text) || text.Length < 16) return text;

            var hits = 0;
            var result = BankCardPattern.Replace(text, match =>
            {
                if (!IsValidBankCard(match.Value)) return match.Value;
                hits++;
                var value = match.Value;
                return value[..4] + new string('*', value.Length - 8) + value[^4..];
            });

            count = hits;
            return result;
        }

        /// <summary>
        /// 16–19 位、Luhn 校验通过、卡组织首位在 3–6，且不是身份证号。
        /// 注意：18 位纯数字的订单号仍有约 4% 概率碰巧通过 Luhn，属于已知的误伤边界。
        /// </summary>
        public static bool IsValidBankCard(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 16 || s.Length > 19) return false;
            if (s[0] < '3' || s[0] > '6') return false;
            if (IsValidIdCard(s)) return false;

            var sum = 0;
            var doubleIt = false;
            for (var i = s.Length - 1; i >= 0; i--)
            {
                var c = s[i];
                if (c < '0' || c > '9') return false;
                var digit = c - '0';
                if (doubleIt)
                {
                    digit *= 2;
                    if (digit > 9) digit -= 9;
                }
                sum += digit;
                doubleIt = !doubleIt;
            }

            return sum % 10 == 0;
        }
    }
}
