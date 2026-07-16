// Ported from tsgo internal/ast/utilities.go — binding/assignment-element target + property-name
// helpers. These are shared by the transformers (destructuring) and the printer (rest helper), so
// their faithful home is the ast package.

namespace Ts.Ast;

public static partial class AstUtilities
{
    // ast/utilities.go — IsDeclarationBindingElement.
    public static bool IsDeclarationBindingElement(Node bindingElement) => bindingElement.Kind is
        SyntaxKind.VariableDeclaration or SyntaxKind.Parameter or SyntaxKind.BindingElement;

    // ast/utilities.go:3830 — GetTargetOfBindingOrAssignmentElement.
    public static Node? GetTargetOfBindingOrAssignmentElement(Node? bindingElement)
    {
        if (bindingElement is null)
        {
            return null;
        }
        if (IsDeclarationBindingElement(bindingElement))
        {
            return bindingElement.Name();
        }
        if (IsObjectLiteralElement(bindingElement))
        {
            return bindingElement.Kind switch
            {
                SyntaxKind.PropertyAssignment => GetTargetOfBindingOrAssignmentElement(bindingElement.Initializer()),
                SyntaxKind.ShorthandPropertyAssignment => bindingElement.Name(),
                SyntaxKind.SpreadAssignment => GetTargetOfBindingOrAssignmentElement(bindingElement.Expression()),
                _ => null,
            };
        }
        if (IsAssignmentExpression(bindingElement, true))
        {
            return GetTargetOfBindingOrAssignmentElement(bindingElement.AsBinaryExpression().Left);
        }
        if (NodeTests.IsSpreadElement(bindingElement))
        {
            return GetTargetOfBindingOrAssignmentElement(bindingElement.Expression());
        }
        return bindingElement;
    }

    // ast/utilities.go:3900 — TryGetPropertyNameOfBindingOrAssignmentElement.
    public static Node? TryGetPropertyNameOfBindingOrAssignmentElement(Node bindingElement)
    {
        if (bindingElement.Kind == SyntaxKind.BindingElement && bindingElement.PropertyName() is { } bindingPropertyName)
        {
            // if IsPrivateIdentifier(propertyName) { return Debug.failBadSyntaxKind(propertyName) } // !!!
            return NodeTests.IsComputedPropertyName(bindingPropertyName) && IsStringOrNumericLiteralLike(bindingPropertyName.Expression()!)
                ? bindingPropertyName.Expression()
                : bindingPropertyName;
        }
        if (bindingElement.Kind == SyntaxKind.PropertyAssignment && bindingElement.Name() is { } assignmentPropertyName)
        {
            // if IsPrivateIdentifier(propertyName) { return Debug.failBadSyntaxKind(propertyName) } // !!!
            return NodeTests.IsComputedPropertyName(assignmentPropertyName) && IsStringOrNumericLiteralLike(assignmentPropertyName.Expression()!)
                ? assignmentPropertyName.Expression()
                : assignmentPropertyName;
        }
        if (bindingElement.Kind == SyntaxKind.SpreadAssignment)
        {
            // if IsPrivateIdentifier(bindingElement.Name()) { return Debug.failBadSyntaxKind(...) } // !!!
            return bindingElement.Name();
        }
        Node? target = GetTargetOfBindingOrAssignmentElement(bindingElement);
        return target is not null && IsPropertyName(target) ? target : null;
    }
}
