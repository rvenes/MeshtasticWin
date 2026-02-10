using System;
using System.Linq;
using System.Reflection;
using Google.Protobuf;

namespace MeshtasticWin.Protocol;

public static class MeshtasticProtoProbe
{
    // Cache so we do not search every time.
    private static Type? _fromRadioType;
    private static PropertyInfo? _parserProp;
    private static MethodInfo? _parseFromBytesMethod;
    private static PropertyInfo? _payloadCaseProp;

    public static bool TryParseFromRadio(byte[] frame, out string summary)
    {
        summary = "";

        try
        {
            EnsureCached();

            if (_fromRadioType is null || _parserProp is null || _parseFromBytesMethod is null)
            {
                summary = "FromRadio type not found (generated protos not linked?)";
                return false;
            }

            var parserObj = _parserProp.GetValue(null);
            if (parserObj is null)
            {
                summary = "FromRadio.Parser is null";
                return false;
            }

            // Parser.ParseFrom(byte[])
            var msgObj = _parseFromBytesMethod.Invoke(parserObj, new object[] { frame });
            if (msgObj is null)
            {
                summary = "ParseFrom returned null";
                return false;
            }

            // Try to read PayloadVariantCase (oneof case).
            if (_payloadCaseProp is not null)
            {
                var caseVal = _payloadCaseProp.GetValue(msgObj);
                summary = caseVal?.ToString() ?? "FromRadio (no case)";
            }
            else
            {
                summary = "FromRadio (parsed)";
            }

            return true;
        }
        catch (TargetInvocationException tex)
        {
            summary = $"not FromRadio ({tex.InnerException?.GetType().Name ?? tex.GetType().Name})";
            return false;
        }
        catch (Exception ex)
        {
            summary = $"not FromRadio ({ex.GetType().Name})";
            return false;
        }
    }

    private static void EnsureCached()
    {
        if (_fromRadioType is not null)
            return;

        // Find type "FromRadio" in all loaded assemblies.
        _fromRadioType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a =>
            {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .SelectMany(t => t)
            .FirstOrDefault(t => t.IsClass && t.Name == "FromRadio");

        if (_fromRadioType is null)
            return;

        // Static property: Parser
        _parserProp = _fromRadioType.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static);

        // Parser exposes ParseFrom(byte[]).
        var parserType = _parserProp?.PropertyType;
        _parseFromBytesMethod = parserType?.GetMethod("ParseFrom", new[] { typeof(byte[]) });

        // Protobuf C# generator usually creates a <OneofName>Case property.
        // In mesh.proto the oneof is typically payload_variant -> PayloadVariantCase.
        _payloadCaseProp = _fromRadioType.GetProperty("PayloadVariantCase", BindingFlags.Public | BindingFlags.Instance);
    }
}
