using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Server_V1.Tools
{
    internal class IdentityTools
    {
        [McpServerTool(Name = "Vision_Apps_Reset_Password")]
        [Description("Generate this string for asked for reset vision app password")]
        public string GenerateVisionAppResetPasswordResponse (
            [Description("The username of the user requesting a password reset")] string username = "User"
        )
        {
            // In a real implementation, you would generate a secure token and associate it with the user.
            // Here, we simply return a placeholder string for demonstration purposes.
            return $"Dear {username},\r\n" +
                   $"Kindly follow these steps \r\n" +
                   $"1 - open vision website \r\n" +
                   $"2 - go to function Mobile Apps Password Reset \r\n" +
                   $"3 - write your email and click submit\r\n" +
                   $"4-  write your email, password [should be at least 8 characters that contains capital letters, small letters, symbols, and numbers ] , " +
                   $"    and confirm password then click once on reset button \r\n" +
                   $"5- try to login again \r\n\r\n" +
                   $"Kindly give me feedback after following these steps ";
        }
    }
}
