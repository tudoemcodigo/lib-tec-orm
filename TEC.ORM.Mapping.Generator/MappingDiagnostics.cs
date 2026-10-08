using Microsoft.CodeAnalysis;

namespace TEC.ORM.Mapping.Generator;

/// <summary>Erros de compilação do mapeamento (TECORM001 a TECORM016). Todo erro impede a geração do DTO afetado.</summary>
internal static class MappingDiagnostics
{
    private const string Category = "TEC.ORM.Mapping";

    private static DiagnosticDescriptor Error(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotPartial = Error("TECORM001",
        "DTO mapeado precisa ser partial",
        "O DTO '{0}' tem [MapFrom] e precisa ser declarado como partial para receber o mapeamento gerado");

    public static readonly DiagnosticDescriptor NoSource = Error("TECORM002",
        "Propriedade do DTO sem origem",
        "A propriedade '{0}' do DTO '{1}' não existe na entidade '{2}'; use [MapFrom(\"caminho\")] para indicar a origem ou [MapIgnore] para ignorá-la");

    public static readonly DiagnosticDescriptor InvalidPath = Error("TECORM003",
        "Caminho de origem inválido",
        "O caminho '{0}' da propriedade '{1}' é inválido: {2}");

    public static readonly DiagnosticDescriptor IncompatibleTypes = Error("TECORM004",
        "Tipos incompatíveis",
        "A propriedade '{0}' ({1}) não pode receber '{2}' ({3}) sem perda; use [MapConverter] ou ajuste o tipo");

    public static readonly DiagnosticDescriptor Cycle = Error("TECORM005",
        "Ciclo entre DTOs sem MaxDepth",
        "O DTO '{0}' entra em ciclo pelo caminho {1}; defina MaxDepth em [MapObject]/[MapList] de alguma propriedade do ciclo");

    public static readonly DiagnosticDescriptor NotMappedDto = Error("TECORM006",
        "Tipo aninhado não é um DTO mapeado",
        "A propriedade '{0}' usa o tipo '{1}', que {2}");

    public static readonly DiagnosticDescriptor InvalidConverter = Error("TECORM007",
        "Conversor inválido",
        "O conversor '{0}' da propriedade '{1}' {2}");

    public static readonly DiagnosticDescriptor InvalidSync = Error("TECORM008",
        "Sincronização impossível",
        "A propriedade '{0}' não pode usar Sync = true: {1}");

    public static readonly DiagnosticDescriptor NotWritable = Error("TECORM009",
        "Propriedade do DTO sem set/init",
        "A propriedade '{0}' do DTO '{1}' precisa de set ou init para ser preenchida; use [MapIgnore] se ela for calculada");

    public static readonly DiagnosticDescriptor UnsupportedCollection = Error("TECORM010",
        "Coleção não suportada",
        "A propriedade '{0}' ({1}) {2}");

    public static readonly DiagnosticDescriptor UnsupportedDtoShape = Error("TECORM011",
        "Forma de DTO não suportada",
        "O DTO '{0}' não pode ser {1}");

    public static readonly DiagnosticDescriptor InvalidDeclaration = Error("TECORM012",
        "Declaração de mapeamento inválida",
        "O mapeamento do DTO '{0}' é inválido: {1}");

    public static readonly DiagnosticDescriptor GeneratedMemberConflict = Error("TECORM013",
        "Membro do DTO colide com o código gerado",
        "O membro '{0}' do DTO '{1}' tem o nome de um membro gerado pelo mapeamento (Projection, FromEntity, ApplyTo, ToEntity ou __TecOrm*); renomeie-o");

    public static readonly DiagnosticDescriptor UnsupportedEntity = Error("TECORM015",
        "Entidade não suportada",
        "A entidade '{0}' do DTO '{1}' não pode ser mapeada: {2}");

    public static readonly DiagnosticDescriptor InternalError = Error("TECORM016",
        "Falha interna do gerador de mapeamento",
        "O gerador do TEC.ORM falhou ao processar o DTO '{0}' ({1}: {2}); o mapeamento não foi gerado. Relate o problema com o trecho do DTO");
}
