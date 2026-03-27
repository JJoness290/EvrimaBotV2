using System.Reflection;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: RconCli.exe <ip> <port> <password> <command>");
            return 2;
        }

        var ip = args[0];
        if (!int.TryParse(args[1], out var port))
        {
            Console.Error.WriteLine("Invalid port.");
            return 2;
        }

        var password = args[2];
        var command = string.Join(" ", args.Skip(3)).Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            Console.Error.WriteLine("Command cannot be empty.");
            return 2;
        }

        try
        {
            var clientType = FindClientType();
            if (clientType is null)
            {
                Console.Error.WriteLine("Unable to find EvrimaRconClient in loaded assemblies.");
                return 1;
            }

            var client = CreateClient(clientType, ip, port);
            if (client is null)
            {
                Console.Error.WriteLine("Unable to construct EvrimaRconClient with discovered constructors.");
                return 1;
            }

            var connectResult = await InvokeAsyncMember(client, "ConnectAsync", null, TimeSpan.FromSeconds(10));
            if (!IsSuccess(connectResult))
            {
                Console.Error.WriteLine("Connection failure.");
                return 3;
            }

            var authResult = await InvokeAsyncMember(client, "AuthorizeAsync", password, TimeSpan.FromSeconds(10));
            if (!IsSuccess(authResult))
            {
                Console.Error.WriteLine("Auth failure.");
                return 4;
            }

            var sendResponse = await SendCommand(client, command, TimeSpan.FromSeconds(10));
            Console.WriteLine(ToOutput(sendResponse));
            return 0;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("Timeout.");
            return 5;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is TimeoutException)
        {
            Console.Error.WriteLine("Timeout.");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Type? FindClientType()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("TheIsleEvrimaRconClient.EvrimaRconClient", throwOnError: false, ignoreCase: false))
            .FirstOrDefault(t => t is not null);
    }

    private static object? CreateClient(Type clientType, string ip, int port)
    {
        var constructors = clientType.GetConstructors();
        foreach (var ctor in constructors)
        {
            var parameters = ctor.GetParameters();
            try
            {
                if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(int))
                {
                    return ctor.Invoke(new object[] { ip, port });
                }

                if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(ushort))
                {
                    return ctor.Invoke(new object[] { ip, checked((ushort)port) });
                }

                if (parameters.Length == 3 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(int))
                {
                    return ctor.Invoke(new object[] { ip, port, false });
                }
            }
            catch
            {
                // Try next constructor.
            }
        }

        return null;
    }

    private static async Task<object?> SendCommand(object client, string command, TimeSpan timeout)
    {
        var methods = client.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "SendCommandAsync")
            .ToList();

        Exception? lastError = null;
        foreach (var method in methods)
        {
            var args = TryBuildSendCommandArgs(method.GetParameters(), command);
            if (args is null)
            {
                continue;
            }

            try
            {
                return await InvokeMethodAsync(client, method, args, timeout);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException($"No compatible SendCommandAsync overload found. Last error: {lastError?.Message}");
    }

    private static object?[]? TryBuildSendCommandArgs(ParameterInfo[] parameters, string rawCommand)
    {
        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
        {
            return new object?[] { rawCommand };
        }

        if (parameters.Length == 1 && parameters[0].ParameterType.IsEnum)
        {
            var token = rawCommand.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
            var enumValue = ParseEnum(parameters[0].ParameterType, token);
            return enumValue is null ? null : new object?[] { enumValue };
        }

        if (parameters.Length == 2 && parameters[0].ParameterType.IsEnum && parameters[1].ParameterType == typeof(string))
        {
            var parts = rawCommand.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            var enumValue = ParseEnum(parameters[0].ParameterType, parts[0]);
            if (enumValue is null)
            {
                return null;
            }

            var arg = parts.Length > 1 ? parts[1] : string.Empty;
            return new object?[] { enumValue, arg };
        }

        return null;
    }

    private static object? ParseEnum(Type enumType, string token)
    {
        token = token.Trim().TrimStart('/');

        foreach (var name in Enum.GetNames(enumType))
        {
            if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse(enumType, name);
            }
        }

        return null;
    }

    private static async Task<object?> InvokeAsyncMember(object target, string memberName, string? stringArg, TimeSpan timeout)
    {
        var methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == memberName)
            .ToList();

        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                return await InvokeMethodAsync(target, method, Array.Empty<object?>(), timeout);
            }

            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string) && stringArg is not null)
            {
                return await InvokeMethodAsync(target, method, new object?[] { stringArg }, timeout);
            }
        }

        throw new InvalidOperationException($"Unable to invoke {memberName} with discovered signatures.");
    }

    private static async Task<object?> InvokeMethodAsync(object target, MethodInfo method, object?[] args, TimeSpan timeout)
    {
        var result = method.Invoke(target, args);
        if (result is Task task)
        {
            await task.WaitAsync(timeout);
            var resultProperty = task.GetType().GetProperty("Result");
            return resultProperty?.GetValue(task);
        }

        return result;
    }

    private static bool IsSuccess(object? result)
    {
        if (result is null)
        {
            return true;
        }

        if (result is bool b)
        {
            return b;
        }

        var successProperty = result.GetType().GetProperty("Success") ?? result.GetType().GetProperty("IsSuccess");
        if (successProperty?.PropertyType == typeof(bool))
        {
            return (bool)(successProperty.GetValue(result) ?? false);
        }

        return true;
    }

    private static string ToOutput(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string s)
        {
            return s;
        }

        var type = value.GetType();
        var preferredProperty = type.GetProperty("Response")
                                ?? type.GetProperty("Message")
                                ?? type.GetProperty("Data")
                                ?? type.GetProperty("Result");

        if (preferredProperty is not null)
        {
            var propertyValue = preferredProperty.GetValue(value);
            if (propertyValue is not null)
            {
                return propertyValue.ToString() ?? string.Empty;
            }
        }

        return value.ToString() ?? string.Empty;
    }
}
