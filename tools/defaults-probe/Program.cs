// Reads the MelonPreferences defaults compiled into a shipped mod DLL.
//
// Pattern in the IL of every CreateEntry call site: ldstr "Key", then the
// default as a constant (ldc.r4 / ldc.i4.* / ldstr / ldc.r8), and a call to a
// method named CreateEntry a few instructions later. Only keys whose ldstr is
// directly followed by a constant AND reach a CreateEntry call are reported.
//
// Usage: DefaultsProbe <dll> [<dll> ...]   -> "dll<TAB>key<TAB>value" lines
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

foreach (var path in args)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();

    foreach (var handle in md.MethodDefinitions)
    {
        var method = md.GetMethodDefinition(handle);
        if (method.RelativeVirtualAddress == 0)
            continue;

        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        if (il is null)
            continue;

        var list = Decode(il);

        for (var index = 0; index < list.Count; index++)
        {
            var (op, operand) = list[index];
            if (op != 0x72 || index + 1 >= list.Count)
                continue;

            var key = md.GetUserString(MetadataTokens.UserStringHandle((int)(operand & 0xFFFFFF)));
            if (key.Length == 0 || !char.IsUpper(key[0]) || key.Contains(' '))
                continue;

            var value = Constant(md, list[index + 1]);
            if (value is null)
                continue;

            var reaches = false;
            for (var ahead = index + 2; ahead < list.Count && ahead < index + 40; ahead++)
            {
                var (aop, aoperand) = list[ahead];
                if (aop != 0x28 && aop != 0x6F)
                    continue;

                if (CalledName(md, (int)aoperand) == "CreateEntry")
                    reaches = true;
                break;
            }

            if (reaches)
                Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)))}\t{key}\t{value}");
        }
    }
}

static string? Constant(MetadataReader md, (int op, long operand) instr)
{
    var (op, operand) = instr;
    if (op >= 0x15 && op <= 0x1E) return (op - 0x16).ToString(CultureInfo.InvariantCulture);
    if (op == 0x1F || op == 0x20) return operand.ToString(CultureInfo.InvariantCulture);
    if (op == 0x22) return BitConverter.Int32BitsToSingle((int)operand).ToString("R", CultureInfo.InvariantCulture);
    if (op == 0x23) return BitConverter.Int64BitsToDouble(operand).ToString("R", CultureInfo.InvariantCulture);
    if (op == 0x72) return "\"" + md.GetUserString(MetadataTokens.UserStringHandle((int)(operand & 0xFFFFFF))) + "\"";
    return null;
}

static string? CalledName(MetadataReader md, int token)
{
    var handle = MetadataTokens.EntityHandle(token);
    switch (handle.Kind)
    {
        case HandleKind.MemberReference:
            return md.GetString(md.GetMemberReference((MemberReferenceHandle)handle).Name);
        case HandleKind.MethodSpecification:
            var spec = md.GetMethodSpecification((MethodSpecificationHandle)handle);
            return CalledName(md, MetadataTokens.GetToken(spec.Method));
        case HandleKind.MethodDefinition:
            return md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)handle).Name);
        default:
            return null;
    }
}

static List<(int op, long operand)> Decode(byte[] il)
{
    var list = new List<(int, long)>();
    var at = 0;

    while (at < il.Length)
    {
        int op = il[at++];
        long operand = 0;
        var size = 0;

        if (op == 0xFE)
        {
            var second = il[at++];
            op = 0xFE00 | second;
            size = second switch
            {
                0x06 or 0x07 or 0x15 or 0x16 or 0x1C => 4,
                >= 0x09 and <= 0x0E => 2,
                0x12 or 0x19 => 1,
                _ => 0,
            };
        }
        else
        {
            size = op switch
            {
                >= 0x0E and <= 0x13 => 1,
                0x1F => 1,
                0x20 or 0x22 => 4,
                0x21 or 0x23 => 8,
                0x27 or 0x28 or 0x29 => 4,
                >= 0x2B and <= 0x37 => 1,
                >= 0x38 and <= 0x44 => 4,
                0x6F or (>= 0x70 and <= 0x75) or 0x79 or (>= 0x7B and <= 0x81) => 4,
                0x8C or 0x8D or 0x8F or (>= 0xA3 and <= 0xA5) or 0xC2 or 0xC6 or 0xD0 or 0xDD => 4,
                0xDE => 1,
                _ => 0,
            };

            if (op == 0x45)
            {
                var count = BitConverter.ToInt32(il, at);
                at += 4 + 4 * count;
                list.Add((op, 0));
                continue;
            }
        }

        for (var b = 0; b < size && at + b < il.Length; b++)
            operand |= (long)il[at + b] << (8 * b);

        if (size == 1 && (op == 0x1F))
            operand = (sbyte)il[at];

        at += size;
        list.Add((op, operand));
    }

    return list;
}
