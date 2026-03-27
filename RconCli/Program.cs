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
        var rawCommand = string.Join(" ", args.Skip(3)).Trim();
        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            Console.Error.WriteLine("Command cannot be empty.");
            return 2;
        }
        var parsed = ParseCommand(rawCommand);
        Console.Error.WriteLine($"[DEBUG] Parsed command verb: {parsed.Verb}");
        Console.Error.WriteLine($"[DEBUG] Parsed command argument: {parsed.Argument}");

        try
        {
            PrintLoadedAssemblies();

            var clientAssembly = ResolveAssembly("TheIsleEvrimaRconClient");
            var extensionAssembly = ResolveAssembly("TheIsleEvrimaRconClient.Extensions");

            PrintExportedTypes(clientAssembly, "TheIsleEvrimaRconClient");
            PrintExportedTypes(extensionAssembly, "TheIsleEvrimaRconClient.Extensions");

            var clientType = RequireType(clientAssembly, "TheIsleEvrimaRconClient.EvrimaRconClient");
            var commandType = RequireType(clientAssembly, "TheIsleEvrimaRconClient.EvrimaRconCommand");
            var extensionsType = RequireType(extensionAssembly, "TheIsleEvrimaRconClient.Extensions.EvrimaRconClientExtensions");

            PrintConstructors(clientType, "EvrimaRconClient constructors");
            PrintMethods(clientType, "EvrimaRconClient public instance methods", BindingFlags.Instance | BindingFlags.Public);
            PrintMethods(extensionsType, "EvrimaRconClientExtensions public static methods", BindingFlags.Static | BindingFlags.Public);
            PrintCommandMembers(commandType);
            PrintConstructors(commandType, "EvrimaRconCommand constructors");

            var client = CreateClient(clientType, ip, port, password);
            if (client is null)
            {
                Console.Error.WriteLine("Unable to construct TheIsleEvrimaRconClient.EvrimaRconClient with discovered constructors.");
                return 1;
            }

            await InvokeBestConnect(client, TimeSpan.FromSeconds(10));
            await InvokeBestAuthenticate(client, password, TimeSpan.FromSeconds(10));

            var response = await InvokeBestSend(
                client,
                clientType,
                commandType,
                extensionsType,
                parsed.Verb,
                parsed.Argument,
                rawCommand,
                TimeSpan.FromSeconds(10));
            Console.WriteLine(ToOutput(response));
            return 0;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("Timeout.");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void PrintLoadedAssemblies()
    {
        Console.Error.WriteLine("[DEBUG] Loaded assemblies:");
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"[DEBUG] - {assembly.FullName}");
        }
    }

    private static Assembly ResolveAssembly(string simpleName)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));

        if (loaded is not null)
        {
            Console.Error.WriteLine($"[DEBUG] Matched assembly already loaded: {loaded.FullName}");
            return loaded;
        }

        var assembly = Assembly.Load(simpleName);
        Console.Error.WriteLine($"[DEBUG] Matched assembly loaded by name '{simpleName}': {assembly.FullName}");
        return assembly;
    }

    private static void PrintExportedTypes(Assembly assembly, string label)
    {
        Console.Error.WriteLine($"[DEBUG] Exported types from {label}:");
        var exported = assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).ToArray();

        if (exported.Length == 0)
        {
            Console.Error.WriteLine("[DEBUG] - <none>");
            return;
        }

        foreach (var type in exported)
        {
            Console.Error.WriteLine($"[DEBUG] - {type.FullName}");
        }
    }

    private static Type RequireType(Assembly assembly, string fullName)
    {
        var type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
        if (type is null)
        {
            throw new InvalidOperationException($"Type not found: {fullName}");
        }

        Console.Error.WriteLine($"[DEBUG] Matched type: {type.FullName}");
        return type;
    }

    private static void PrintConstructors(Type type, string label)
    {
        Console.Error.WriteLine($"[DEBUG] {label}:");
        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            Console.Error.WriteLine($"[DEBUG] - {FormatConstructorSignature(ctor)}");
        }
    }

    private static void PrintMethods(Type type, string label, BindingFlags flags)
    {
        Console.Error.WriteLine($"[DEBUG] {label}:");
        foreach (var method in type.GetMethods(flags).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"[DEBUG] - {FormatMethodSignature(method)}");
        }
    }

    private static void PrintCommandMembers(Type commandType)
    {
        Console.Error.WriteLine("[DEBUG] EvrimaRconCommand public properties:");
        foreach (var prop in commandType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            Console.Error.WriteLine($"[DEBUG] - {GetFriendlyTypeName(prop.PropertyType)} {prop.Name}");
        }

        Console.Error.WriteLine("[DEBUG] EvrimaRconCommand public fields:");
        foreach (var field in commandType.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            Console.Error.WriteLine($"[DEBUG] - {GetFriendlyTypeName(field.FieldType)} {field.Name}");
        }
    }

    private static object? CreateClient(Type clientType, string ip, int port, string password)
    {
        foreach (var ctor in clientType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                     .OrderByDescending(c => c.GetParameters().Length))
        {
            if (!TryBuildCtorArgs(ctor.GetParameters(), ip, port, password, out var args))
            {
                continue;
            }

            try
            {
                var instance = ctor.Invoke(args);
                Console.Error.WriteLine($"[DEBUG] Selected constructor: {FormatConstructorSignature(ctor)}");
                return instance;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] Constructor failed: {FormatConstructorSignature(ctor)} | {ex.Message}");
            }
        }

        return null;
    }

    private static bool TryBuildCtorArgs(ParameterInfo[] parameters, string ip, int port, string password, out object?[] args)
    {
        args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var pt = p.ParameterType;
            var name = (p.Name ?? string.Empty).ToLowerInvariant();

            if (pt == typeof(string))
            {
                if (name.Contains("pass") || name.Contains("auth") || name.Contains("token"))
                {
                    args[i] = password;
                }
                else
                {
                    args[i] = ip;
                }
                continue;
            }

            if (pt == typeof(int))
            {
                args[i] = port;
                continue;
            }

            if (pt == typeof(ushort))
            {
                args[i] = checked((ushort)port);
                continue;
            }

            if (pt == typeof(bool))
            {
                args[i] = false;
                continue;
            }

            if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
                continue;
            }

            if (!pt.IsValueType)
            {
                args[i] = null;
                continue;
            }

            return false;
        }

        return true;
    }

    private static (string Verb, string Argument) ParseCommand(string rawCommand)
    {
        var parts = rawCommand.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts.Length > 0 ? parts[0].Trim() : string.Empty;
        var argument = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        return (verb, argument);
    }

    private static async Task InvokeBestConnect(object client, TimeSpan timeout)
    {
        var methods = client.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
        var connect = methods
            .Where(m => IsConnectLike(m) && m.GetParameters().Length == 0)
            .OrderByDescending(m => ScoreMethodName(m.Name, "connect", "reconnect", "open"))
            .FirstOrDefault();

        if (connect is null)
        {
            Console.Error.WriteLine("[DEBUG] No connect-like method found; continuing without explicit connect call.");
            return;
        }

        Console.Error.WriteLine($"[DEBUG] Selected connect method: {FormatMethodSignature(connect)}");
        await InvokeMethodAsync(client, connect, Array.Empty<object?>(), timeout);
    }

    private static async Task InvokeBestAuthenticate(object client, string password, TimeSpan timeout)
    {
        var methods = client.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
        var auth = methods
            .Where(m => IsAuthLike(m) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
            .OrderByDescending(m => ScoreMethodName(m.Name, "authoriz", "auth", "login", "password"))
            .FirstOrDefault();

        if (auth is null)
        {
            Console.Error.WriteLine("[DEBUG] No auth-like method found; assuming constructor or connect handled auth.");
            return;
        }

        Console.Error.WriteLine($"[DEBUG] Selected auth method: {FormatMethodSignature(auth)}");
        await InvokeMethodAsync(client, auth, new object?[] { password }, timeout);
    }

    private static async Task<object?> InvokeBestSend(
        object client,
        Type clientType,
        Type commandType,
        Type extensionsType,
        string commandVerb,
        string commandArgument,
        string rawCommand,
        TimeSpan timeout)
    {
        if (string.Equals(commandVerb, "announce", StringComparison.OrdinalIgnoreCase))
        {
            var announceMethods = extensionsType
                .GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m =>
                    string.Equals(m.Name, "Announce", StringComparison.OrdinalIgnoreCase) &&
                    m.GetParameters().Length >= 2 &&
                    m.GetParameters()[0].ParameterType.IsAssignableFrom(clientType))
                .OrderBy(m => m.GetParameters().Length)
                .ToList();

            foreach (var method in announceMethods)
            {
                var ps = method.GetParameters();
                var args = new object?[ps.Length];
                args[0] = client;

                var valid = true;
                for (var i = 1; i < ps.Length; i++)
                {
                    var p = ps[i];
                    if (p.ParameterType == typeof(string))
                    {
                        args[i] = commandArgument;
                    }
                    else if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                    }
                    else if (!p.ParameterType.IsValueType)
                    {
                        args[i] = null;
                    }
                    else
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                {
                    continue;
                }

                try
                {
                    Console.Error.WriteLine($"[DEBUG] Selected announce extension method: {FormatMethodSignature(method)}");
                    return await InvokeMethodAsync(null, method, args, timeout);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DEBUG] Announce extension failed: {FormatMethodSignature(method)} | {ex.Message}");
                }
            }
        }

        var instanceCandidates = clientType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(IsSendLike)
            .OrderByDescending(m => ScoreMethodName(m.Name, "sendcommand", "send", "execute", "command"))
            .ToList();

        foreach (var method in instanceCandidates)
        {
            if (!TryBuildInvocationArgs(method.GetParameters(), clientType, commandType, client, commandVerb, commandArgument, rawCommand, out var args))
            {
                continue;
            }

            try
            {
                Console.Error.WriteLine($"[DEBUG] Selected send instance method: {FormatMethodSignature(method)}");
                return await InvokeMethodAsync(client, method, args, timeout);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] Instance send failed: {FormatMethodSignature(method)} | {ex.Message}");
            }
        }

        var extensionCandidates = extensionsType
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Where(m => IsSendLike(m) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType.IsAssignableFrom(clientType))
            .OrderByDescending(m => ScoreMethodName(m.Name, "sendcommand", "send", "execute", "command"))
            .ToList();

        foreach (var method in extensionCandidates)
        {
            if (!TryBuildInvocationArgs(method.GetParameters(), clientType, commandType, client, commandVerb, commandArgument, rawCommand, out var args))
            {
                continue;
            }

            try
            {
                Console.Error.WriteLine($"[DEBUG] Selected send extension method: {FormatMethodSignature(method)}");
                return await InvokeMethodAsync(null, method, args, timeout);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DEBUG] Extension send failed: {FormatMethodSignature(method)} | {ex.Message}");
            }
        }

        throw new InvalidOperationException("Unable to find a compatible send command method on client or extensions.");
    }

    private static bool TryBuildInvocationArgs(
        ParameterInfo[] parameters,
        Type clientType,
        Type commandType,
        object client,
        string commandVerb,
        string commandArgument,
        string rawCommand,
        out object?[] args)
    {
        args = new object?[parameters.Length];
        var commandName = commandVerb;
        var commandArg = commandArgument;

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var pt = p.ParameterType;

            if (i == 0 && pt.IsAssignableFrom(clientType))
            {
                args[i] = client;
                continue;
            }

            if (pt == typeof(string))
            {
                var name = (p.Name ?? string.Empty).ToLowerInvariant();
                if (name.Contains("arg") || name.Contains("message") || name.Contains("value") || name.Contains("text"))
                {
                    args[i] = commandArg;
                }
                else if (name.Contains("verb") || name.Contains("name") || name.Contains("command"))
                {
                    args[i] = commandName;
                }
                else
                {
                    args[i] = string.IsNullOrWhiteSpace(commandArg) ? commandName : commandArg;
                }
                continue;
            }

            if (pt == commandType)
            {
                if (!TryCreateCommandObject(commandType, commandName, commandArg, rawCommand, out var cmd))
                {
                    return false;
                }

                args[i] = cmd;
                continue;
            }

            if (pt.IsEnum)
            {
                var enumValue = ParseEnum(pt, commandName);
                if (enumValue is null)
                {
                    return false;
                }

                args[i] = enumValue;
                continue;
            }

            if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
                continue;
            }

            if (!pt.IsValueType)
            {
                args[i] = null;
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryCreateCommandObject(Type commandType, string commandName, string commandArg, string rawCommand, out object? command)
    {
        command = null;

        foreach (var ctor in commandType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(c => c.GetParameters().Length))
        {
            var ps = ctor.GetParameters();
            try
            {
                if (ps.Length == 0)
                {
                    command = ctor.Invoke(Array.Empty<object?>());
                    break;
                }

                if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                {
                    command = ctor.Invoke(new object?[] { commandName });
                    break;
                }

                if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                {
                    command = ctor.Invoke(new object?[] { commandName, commandArg });
                    break;
                }
            }
            catch
            {
                // Try next constructor.
            }
        }

        command ??= Activator.CreateInstance(commandType);
        if (command is null)
        {
            return false;
        }

        SetIfWritable(commandType, command, "Command", commandName);
        SetIfWritable(commandType, command, "Name", commandName);
        SetIfWritable(commandType, command, "Action", commandName);
        SetIfWritable(commandType, command, "Argument", commandArg);
        SetIfWritable(commandType, command, "Value", commandArg);
        SetIfWritable(commandType, command, "Raw", rawCommand);
        SetIfWritable(commandType, command, "Text", rawCommand);

        return true;
    }

    private static void SetIfWritable(Type type, object instance, string propertyName, string value)
    {
        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null && prop.CanWrite && prop.PropertyType == typeof(string))
        {
            prop.SetValue(instance, value);
        }

        var field = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (field is not null && field.FieldType == typeof(string))
        {
            field.SetValue(instance, value);
        }
    }

    private static bool IsConnectLike(MethodInfo m)
    {
        var n = m.Name.ToLowerInvariant();
        return n.Contains("connect") || n.Contains("reconnect") || n == "open";
    }

    private static bool IsAuthLike(MethodInfo m)
    {
        var n = m.Name.ToLowerInvariant();
        return n.Contains("auth") || n.Contains("authoriz") || n.Contains("login") || n.Contains("password");
    }

    private static bool IsSendLike(MethodInfo m)
    {
        var n = m.Name.ToLowerInvariant();
        return n.Contains("send") || n.Contains("command") || n.Contains("execute") || n.Contains("announce");
    }

    private static int ScoreMethodName(string name, params string[] preferred)
    {
        var lower = name.ToLowerInvariant();
        var score = 0;
        for (var i = 0; i < preferred.Length; i++)
        {
            if (lower.Contains(preferred[i]))
            {
                score += 100 - i;
            }
        }

        return score;
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

    private static async Task<object?> InvokeMethodAsync(object? target, MethodInfo method, object?[] args, TimeSpan timeout)
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

    private static string FormatConstructorSignature(ConstructorInfo ctor)
    {
        var parameters = string.Join(", ", ctor.GetParameters().Select(FormatParameter));
        return $"{ctor.DeclaringType?.FullName}({parameters})";
    }

    private static string FormatMethodSignature(MethodInfo method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(FormatParameter));
        var returnType = GetFriendlyTypeName(method.ReturnType);
        return $"{returnType} {method.DeclaringType?.FullName}.{method.Name}({parameters})";
    }

    private static string FormatParameter(ParameterInfo p)
    {
        var suffix = p.IsOptional ? " = <optional>" : string.Empty;
        return $"{GetFriendlyTypeName(p.ParameterType)} {p.Name}{suffix}";
    }

    private static string GetFriendlyTypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var genericName = type.GetGenericTypeDefinition().FullName ?? type.Name;
        genericName = genericName.Split('`')[0];
        var args = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
        return $"{genericName}<{args}>";
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
