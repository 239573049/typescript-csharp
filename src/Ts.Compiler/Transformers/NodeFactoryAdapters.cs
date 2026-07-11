// C# adapter for Go's embedded ast.NodeFactory in printer.NodeFactory.
// Go promotes every ast factory method automatically; the landed C# printer factory exposes
// AsNodeFactory(), so missing promoted members are forwarded here without duplicating state.

using Ts.Ast;

namespace Ts.Transformers;

internal static class NodeFactoryAdapters
{
    public static Node NewImportDeclaration(
        this global::Ts.Printer.NodeFactory factory,
        ModifierList? modifiers,
        ImportClauseNode? importClause,
        Expression? moduleSpecifier,
        ImportAttributesNode? attributes) =>
        factory.AsNodeFactory().NewImportDeclaration(modifiers, importClause, moduleSpecifier, attributes);

    public static Node NewImportClause(
        this global::Ts.Printer.NodeFactory factory,
        ImportPhaseModifierSyntaxKind phaseModifier,
        IdentifierNode? name,
        NamedImportBindings? namedBindings) =>
        factory.AsNodeFactory().NewImportClause(phaseModifier, name, namedBindings);

    public static Node NewNamedImports(this global::Ts.Printer.NodeFactory factory, ImportSpecifierList? elements) =>
        factory.AsNodeFactory().NewNamedImports(elements);

    public static Node NewImportSpecifier(
        this global::Ts.Printer.NodeFactory factory,
        bool isTypeOnly,
        ModuleExportName? propertyName,
        IdentifierNode? name) =>
        factory.AsNodeFactory().NewImportSpecifier(isTypeOnly, propertyName, name);

    public static Node UpdateSourceFile(
        this global::Ts.Printer.NodeFactory factory,
        SourceFile sourceFile,
        StatementList? statements,
        TokenNode? endOfFileToken) =>
        factory.AsNodeFactory().UpdateSourceFile(sourceFile, statements, endOfFileToken);

    public static Node NewImportEqualsDeclaration(
        this global::Ts.Printer.NodeFactory factory,
        ModifierList? modifiers,
        bool isTypeOnly,
        IdentifierNode? name,
        ModuleReference? moduleReference) =>
        factory.AsNodeFactory().NewImportEqualsDeclaration(modifiers, isTypeOnly, name, moduleReference);

    public static Node NewExternalModuleReference(this global::Ts.Printer.NodeFactory factory, Expression? expression) =>
        factory.AsNodeFactory().NewExternalModuleReference(expression);

    public static Node NewNamespaceImport(this global::Ts.Printer.NodeFactory factory, IdentifierNode? name) =>
        factory.AsNodeFactory().NewNamespaceImport(name);

    public static Node UpdateImportDeclaration(
        this global::Ts.Printer.NodeFactory factory,
        ImportDeclaration node,
        ModifierList? modifiers,
        ImportClauseNode? importClause,
        Expression? moduleSpecifier,
        ImportAttributesNode? attributes) =>
        factory.AsNodeFactory().UpdateImportDeclaration(node, modifiers, importClause, moduleSpecifier, attributes);

    public static Node UpdateExportDeclaration(
        this global::Ts.Printer.NodeFactory factory,
        ExportDeclaration node,
        ModifierList? modifiers,
        bool isTypeOnly,
        NamedExportBindings? exportClause,
        Expression? moduleSpecifier,
        ImportAttributesNode? attributes) =>
        factory.AsNodeFactory().UpdateExportDeclaration(node, modifiers, isTypeOnly, exportClause, moduleSpecifier, attributes);

    public static Node UpdateCallExpression(
        this global::Ts.Printer.NodeFactory factory,
        CallExpression node,
        Expression? expression,
        QuestionDotToken? questionDotToken,
        TypeList? typeArguments,
        ElementList? arguments,
        NodeFlags flags) =>
        factory.AsNodeFactory().UpdateCallExpression(node, expression, questionDotToken, typeArguments, arguments, flags);

    public static Node NewMetaProperty(this global::Ts.Printer.NodeFactory factory, SyntaxKind keywordToken, IdentifierNode? name) =>
        factory.AsNodeFactory().NewMetaProperty(keywordToken, name);

    public static Node NewEmptyStatement(this global::Ts.Printer.NodeFactory factory) => factory.AsNodeFactory().NewEmptyStatement();
    public static Node UpdateIfStatement(this global::Ts.Printer.NodeFactory f, IfStatement n, Expression? e, Statement? t, Statement? x) => f.AsNodeFactory().UpdateIfStatement(n, e, t, x);
    public static Node UpdateDoStatement(this global::Ts.Printer.NodeFactory f, DoStatement n, Statement? s, Expression? e) => f.AsNodeFactory().UpdateDoStatement(n, s, e);
    public static Node UpdateWhileStatement(this global::Ts.Printer.NodeFactory f, WhileStatement n, Expression? e, Statement? s) => f.AsNodeFactory().UpdateWhileStatement(n, e, s);
    public static Node UpdateForStatement(this global::Ts.Printer.NodeFactory f, ForStatement n, ForInitializer? i, Expression? c, Expression? x, Statement? s) => f.AsNodeFactory().UpdateForStatement(n, i, c, x, s);
    public static Node UpdateForInOrOfStatement(this global::Ts.Printer.NodeFactory f, ForInOrOfStatement n, AwaitKeyword? a, ForInitializer? i, Expression? e, Statement? s) => f.AsNodeFactory().UpdateForInOrOfStatement(n, a, i, e, s);
    public static Node UpdateWithStatement(this global::Ts.Printer.NodeFactory f, WithStatement n, Expression? e, Statement? s) => f.AsNodeFactory().UpdateWithStatement(n, e, s);
    public static Node UpdateSwitchStatement(this global::Ts.Printer.NodeFactory f, SwitchStatement n, Expression? e, CaseBlockNode? c) => f.AsNodeFactory().UpdateSwitchStatement(n, e, c);
    public static Node UpdateCaseOrDefaultClause(this global::Ts.Printer.NodeFactory f, CaseOrDefaultClause n, Expression? e, StatementList? s) => f.AsNodeFactory().UpdateCaseOrDefaultClause(n, e, s);
    public static Node UpdateCatchClause(this global::Ts.Printer.NodeFactory f, CatchClause n, VariableDeclarationNode? v, BlockNode? b) => f.AsNodeFactory().UpdateCatchClause(n, v, b);
    public static Node UpdateVariableStatement(this global::Ts.Printer.NodeFactory f, VariableStatement n, ModifierList? m, VariableDeclarationListNode? d) => f.AsNodeFactory().UpdateVariableStatement(n, m, d);
    public static Node UpdateFunctionDeclaration(this global::Ts.Printer.NodeFactory f, FunctionDeclaration n, ModifierList? m, AsteriskToken? a, IdentifierNode? name, TypeParameterList? tp, ParameterList? p, TypeNode? t, TypeNode? fs, FunctionBody? b) => f.AsNodeFactory().UpdateFunctionDeclaration(n, m, a, name, tp, p, t, fs, b);
    public static Node UpdateClassDeclaration(this global::Ts.Printer.NodeFactory f, ClassDeclaration n, ModifierList? m, IdentifierNode? name, TypeParameterList? tp, HeritageClauseList? h, ClassElementList? members) => f.AsNodeFactory().UpdateClassDeclaration(n, m, name, tp, h, members);
    public static Node UpdateBinaryExpression(this global::Ts.Printer.NodeFactory f, BinaryExpression n, ModifierList? m, Expression? l, TypeNode? t, BinaryOperatorToken? o, Expression? r) => f.AsNodeFactory().UpdateBinaryExpression(n, m, l, t, o, r);
    public static Node UpdatePrefixUnaryExpression(this global::Ts.Printer.NodeFactory f, PrefixUnaryExpression n, SyntaxKind o, Expression? operand) => f.AsNodeFactory().UpdatePrefixUnaryExpression(n, o, operand);
    public static Node UpdatePostfixUnaryExpression(this global::Ts.Printer.NodeFactory f, PostfixUnaryExpression n, Expression? operand, SyntaxKind o) => f.AsNodeFactory().UpdatePostfixUnaryExpression(n, operand, o);
    public static Node UpdateSpreadElement(this global::Ts.Printer.NodeFactory f, SpreadElement n, Expression? e) => f.AsNodeFactory().UpdateSpreadElement(n, e);
    public static Node UpdateSpreadAssignment(this global::Ts.Printer.NodeFactory f, SpreadAssignment n, Expression? e) => f.AsNodeFactory().UpdateSpreadAssignment(n, e);
    public static Node UpdatePropertyAssignment(this global::Ts.Printer.NodeFactory f, PropertyAssignment n, ModifierList? m, PropertyName? name, TokenNode? p, TypeNode? t, Expression? i) => f.AsNodeFactory().UpdatePropertyAssignment(n, m, name, p, t, i);
    public static Node UpdateShorthandPropertyAssignment(this global::Ts.Printer.NodeFactory f, ShorthandPropertyAssignment n, ModifierList? m, PropertyName? name, TokenNode? p, TypeNode? t, EqualsToken? e, Expression? i) => f.AsNodeFactory().UpdateShorthandPropertyAssignment(n, m, name, p, t, e, i);
    public static Node UpdateTaggedTemplateExpression(this global::Ts.Printer.NodeFactory f, TaggedTemplateExpression n, Expression? tag, QuestionDotToken? q, TypeList? t, TemplateLiteral? template, NodeFlags flags) => f.AsNodeFactory().UpdateTaggedTemplateExpression(n, tag, q, t, template, flags);
    public static Node NewTemplateExpression(this global::Ts.Printer.NodeFactory f, TemplateHeadNode? h, TemplateSpanList? s) => f.AsNodeFactory().NewTemplateExpression(h, s);
    public static Node NewTemplateSpan(this global::Ts.Printer.NodeFactory f, Expression? e, TemplateMiddleOrTail? l) => f.AsNodeFactory().NewTemplateSpan(e, l);
    public static Node NewTemplateHead(this global::Ts.Printer.NodeFactory f, string t, string r, TokenFlags flags) => f.AsNodeFactory().NewTemplateHead(t, r, flags);
    public static Node NewTemplateTail(this global::Ts.Printer.NodeFactory f, string t, string r, TokenFlags flags) => f.AsNodeFactory().NewTemplateTail(t, r, flags);

    public static Node NewOmittedExpression(this global::Ts.Printer.NodeFactory factory) =>
        factory.AsNodeFactory().NewOmittedExpression();

    public static Node NewBigIntLiteral(this global::Ts.Printer.NodeFactory factory, string text, TokenFlags flags) =>
        factory.AsNodeFactory().NewBigIntLiteral(text, flags);

    public static Node NewPrefixUnaryExpression(this global::Ts.Printer.NodeFactory factory, SyntaxKind @operator, Expression? operand) =>
        factory.AsNodeFactory().NewPrefixUnaryExpression(@operator, operand);

    public static Node NewSpreadElement(this global::Ts.Printer.NodeFactory factory, Expression? expression) =>
        factory.AsNodeFactory().NewSpreadElement(expression);

    public static Node NewSpreadAssignment(this global::Ts.Printer.NodeFactory factory, Expression? expression) =>
        factory.AsNodeFactory().NewSpreadAssignment(expression);

    public static Node NewShorthandPropertyAssignment(
        this global::Ts.Printer.NodeFactory factory,
        ModifierList? modifiers,
        PropertyName? name,
        TokenNode? postfixToken,
        TypeNode? typeNode,
        EqualsToken? equalsToken,
        Expression? objectAssignmentInitializer) =>
        factory.AsNodeFactory().NewShorthandPropertyAssignment(modifiers, name, postfixToken, typeNode, equalsToken, objectAssignmentInitializer);

    public static Node NewSyntaxList(this global::Ts.Printer.NodeFactory factory, Node[]? children) =>
        factory.AsNodeFactory().NewSyntaxList(children);

    public static Node NewBindingPattern(this global::Ts.Printer.NodeFactory factory, SyntaxKind kind, BindingElementList? elements) =>
        factory.AsNodeFactory().NewBindingPattern(kind, elements);

    public static Node NewBindingElement(
        this global::Ts.Printer.NodeFactory factory,
        DotDotDotToken? dotDotDotToken,
        PropertyName? propertyName,
        BindingName? name,
        Expression? initializer) =>
        factory.AsNodeFactory().NewBindingElement(dotDotDotToken, propertyName, name, initializer);
}
