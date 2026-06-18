using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PythonScript
{
    /// <summary>
    /// 将 C# 三元表达式转换为 Python 语法的重写器。
    /// </summary>
    internal sealed class TernaryExpressionRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            ExpressionSyntax condition = (ExpressionSyntax)Visit(node.Condition);
            ExpressionSyntax whenTrue = (ExpressionSyntax)Visit(node.WhenTrue);
            ExpressionSyntax whenFalse = (ExpressionSyntax)Visit(node.WhenFalse);

            string pythonTernary = $"{whenTrue} if {condition} else {whenFalse}";

            return SyntaxFactory.IdentifierName(pythonTernary)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }

        public override SyntaxNode? VisitParenthesizedExpression(ParenthesizedExpressionSyntax node)
        {
            ExpressionSyntax expression = (ExpressionSyntax)Visit(node.Expression);
            return SyntaxFactory.ParenthesizedExpression(expression)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }
    }
}

