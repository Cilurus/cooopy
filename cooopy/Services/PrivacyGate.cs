using Windows.Security.Credentials.UI;

namespace Cooopy.Services
{
    /// <summary>
    /// 私密记录的查看闸门。借 Windows Hello（PIN / 指纹 / 人脸）做验证，
    /// 不自己维护密码——省掉密码哈希、忘记密码、改密一堆事，安全边界与 Windows 账户一致。
    ///
    /// 注意：这里只挡住“别人趁你不在点开看”，data.json 里私密记录仍是明文，
    /// 拷走文件依然能直接读。要防这个必须对内容做真加密。
    /// </summary>
    public static class PrivacyGate
    {
        /// <summary>验证通过后的免验证时长。</summary>
        private static readonly TimeSpan SessionLength = TimeSpan.FromMinutes(3);

        private static DateTime _unlockedUntil = DateTime.MinValue;

        public static bool IsUnlocked => DateTime.Now < _unlockedUntil;

        public static void Lock() => _unlockedUntil = DateTime.MinValue;

        /// <summary>已解锁直接放行；否则弹系统验证框。</summary>
        public static async Task<(bool ok, string error)> VerifyAsync(string message)
        {
            if (IsUnlocked) return (true, null);

            try
            {
                var availability = await UserConsentVerifier.CheckAvailabilityAsync();
                if (availability != UserConsentVerifierAvailability.Available)
                {
                    return (false, availability switch
                    {
                        UserConsentVerifierAvailability.NotConfiguredForUser =>
                            "请先在 Windows 设置里配置 PIN 或指纹，才能查看私密记录",
                        _ => "当前设备不支持 Windows Hello，无法验证"
                    });
                }

                var result = await UserConsentVerifier.RequestVerificationAsync(message);
                if (result == UserConsentVerificationResult.Verified)
                {
                    _unlockedUntil = DateTime.Now + SessionLength;
                    return (true, null);
                }

                // 用户主动取消不算错误，不必打扰。
                return (false, result == UserConsentVerificationResult.Canceled ? null : "验证未通过");
            }
            catch (Exception ex)
            {
                return (false, "验证失败：" + ex.Message);
            }
        }
    }
}
