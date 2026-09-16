; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TCSG001 | CodeGeneration | Error | BinaryLayoutGenerator
TCSG002 | CodeGeneration | Error | BinaryLayoutGenerator
TCSG003 | CodeGeneration | Error | BinaryLayoutGenerator
TCSG004 | CodeGeneration | Error | BinaryLayoutGenerator
TCSG005 | CodeGeneration | Error | BinaryLayoutGenerator
TCSG006 | CodeGeneration | Error | BinaryLayoutGenerator field overlap
TCSG010 | CodeGeneration | Error | MediumOptions unknown nature
TCSG011 | CodeGeneration | Error | MediumOptions unknown verb
TCSG012 | CodeGeneration | Error | NetworkProtocol invalid key
TCSG013 | CodeGeneration | Error | SpecParam without DSL shape mapping
TCSG014 | CodeGeneration | Error | SpecParam unknown media
TCSG020 | CodeGeneration | Error | RingKey type not unmanaged
TCSG021 | CodeGeneration | Error | KvStore key not unmanaged
TCSG022 | CodeGeneration | Error | KvStore value not formattable
TCSG023 | CodeGeneration | Error | KvStore formatter not implementing IValueFormatter
TCSG040 | CodeGeneration | Error | ConstantRegistry duplicate value
TCSG041 | CodeGeneration | Error | ConstantRegistry invalid zone declaration
TCSG042 | CodeGeneration | Error | ConstantRegistry invalid zone range
TCSG043 | CodeGeneration | Error | ConstantRegistry class not partial
TCSG044 | CodeGeneration | Error | ConstantRegistry zone value domain ambiguous
TCSG045 | CodeGeneration | Error | WireArray array field count
TCSG046 | CodeGeneration | Error | WireArray unsupported item type
TCSG047 | CodeGeneration | Error | WireArray invalid MaxCount
TCSG048 | CodeGeneration | Error | WireArray constructor mismatch
TCSG050 | CodeGeneration | Error | WireMessage duplicate tag
TCSG051 | CodeGeneration | Error | WireMessage unsupported member
TCSG052 | CodeGeneration | Error | WireMessage variable member without MaxCount
TCSG054 | CodeGeneration | Error | Command path conflict (duplicate command/group name, route collision, duplicate arg position)
TCSG055 | CodeGeneration | Error | Illegal command name (empty or contains path separator)
TCSG056 | CodeGeneration | Error | Command parameter not annotated (CommandArg/CommandOption/CommandBody)
TCSG057 | CodeGeneration | Error | Unbindable command parameter form (ref/out/in, pointer, ref struct)
TCSG058 | CodeGeneration | Error | GET command with CommandBody parameter
TCSG059 | CodeGeneration | Error | Illegal command attribute host (non-group type, multiple bodies, non-exception CommandError)
TCSG060 | CodeGeneration | Error | Command parameter name uses generator-reserved local identifier prefix (__tcsg_)
TCSG053 | CodeGeneration | Error | WireMessageTag outside a message family
