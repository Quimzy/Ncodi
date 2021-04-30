﻿using Pital.CodeAnalysis.Syntax;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using Pital.CodeAnalysis.Symbols;
using Pital.CodeAnalysis.Text;

namespace Pital.CodeAnalysis.Binding
{
    internal sealed class Binder
    {
        public readonly DiagnosticBag _diagnostics = new DiagnosticBag();
        private BoundScope _scope;

        public Binder(BoundScope parent)
        {
            _scope = new BoundScope(parent);
        }

        public static BoundGlobalScope BindGlobalScope(BoundGlobalScope previous,CompilationUnitSyntax syntax)
        {
            var parentScope = CreateParentScope(previous);

            var binder = new Binder(parentScope);
            var expression=binder.BindStatement(syntax.Statement);
            var variables = binder._scope.GetDeclaredVariables();
            var diagnostics = binder.Diagnostics.ToImmutableArray();

            if (previous != null)
            {
                diagnostics = diagnostics.InsertRange(0, previous.Diagnostics);
            }

            return new BoundGlobalScope(previous, diagnostics, variables, expression);
        }

        private static BoundScope CreateParentScope(BoundGlobalScope previous) 
        {
            // sub3 -> sub2 -> sub1
            var stack = new Stack<BoundGlobalScope>();
            while(previous != null)
            {
                stack.Push(previous);
                previous = previous.Previous;
            }

            var parent = CreateRootScope();

            while (stack.Count > 0)
            {
                previous = stack.Pop();
                var scope = new BoundScope(parent);
                foreach (var v in previous.Variables)
                {
                    scope.TryDeclareVariable(v);
                }
                parent = scope;
            }
            return parent;
        }

        private static BoundScope CreateRootScope()
        {
            var result = new BoundScope(null);
            
            foreach(var func in BuiltInFunctions.GetAll())
                result.TryDeclareFunction(func);
            return result;
        }

        public DiagnosticBag Diagnostics => _diagnostics;

        private BoundStatement BindStatement(StatementSyntax syntax)
        {
            switch (syntax.Kind)
            {
                case SyntaxKind.BlockStatement:
                    return BindBlockStatement((BlockStatementSyntax)syntax);
                case SyntaxKind.VariableDeclaration:
                    return BindVariableDeclaration((VariableDeclarationSyntax)syntax);
                case SyntaxKind.ExpressionStatement:
                    return BindExpressionStatement((ExpressionStatementSyntax)syntax);
                case SyntaxKind.IfStatement:
                    return BindIfStatement((IfStatementSyntax)syntax);
                case SyntaxKind.WhileStatement:
                    return BindWhileStatement((WhileStatementSyntax)syntax);
                case SyntaxKind.DoWhileStatement:
                    return BindDoWhileStatement((DoWhileStatementSyntax)syntax);
                case SyntaxKind.ForStatement:
                    return BindForStatement((ForStatementSyntax)syntax);
                default:
                    throw new Exception($"Unexpexted Syntax {syntax.Kind}");
            }
        }


        private BoundStatement BindDoWhileStatement(DoWhileStatementSyntax syntax)
        {
            var body = BindStatement(syntax.Body);
            var condition = BindExpression(syntax.Condition, TypeSymbol.Bool);
            return new BoundDoWhileStatement(body, condition);
        }

        private BoundStatement BindVariableDeclaration(VariableDeclarationSyntax syntax)
        {
            var isReadonly = syntax.KeywordToken.Kind == SyntaxKind.ConstKeyword;
            var initializer = BindExpression(syntax.Initializer);
            var variable = BindVariable(syntax.Identifier, isReadonly, initializer.Type);

            return new BoundVariableDeclaration(variable, initializer);
        }

        private BoundStatement BindBlockStatement(BlockStatementSyntax syntax)
        {
            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            _scope = new BoundScope(_scope);

            foreach(var statementSyntax in syntax.Statements)
            {
                var statement = BindStatement(statementSyntax);
                statements.Add(statement);
            }

            _scope = _scope.Parent;
            return new BoundBlockStatement(statements.ToImmutable());
        }

        private BoundStatement BindExpressionStatement(ExpressionStatementSyntax syntax)
        {
            var expression = BindExpression(syntax.Expression,canBeVoid:true);
            return new BoundExpressionStatement(expression);
        }

        private BoundStatement BindIfStatement(IfStatementSyntax syntax)
        {
            var condition = BindExpression(syntax.Condition, TypeSymbol.Bool);
            var thenStatement = BindStatement(syntax.ThenStatement);
            var elseStatement = syntax.ElseClause == null ? null : BindStatement(syntax.ElseClause.ElseStatement);
            return new BoundIfStatement(condition, thenStatement, elseStatement);
        }
        private BoundStatement BindWhileStatement(WhileStatementSyntax syntax)
        {
            var condition = BindExpression(syntax.Condition, TypeSymbol.Bool);
            var body = BindStatement(syntax.Body);
            return new BoundWhileStatement(condition, body);
        }
        private BoundStatement BindForStatement(ForStatementSyntax syntax)
        {
            var lowerBound = BindExpression(syntax.LowerBound, TypeSymbol.Int);
            var upperBound = BindExpression(syntax.UpperBound, TypeSymbol.Int);

            _scope = new BoundScope(_scope);

            var variable = BindVariable(syntax.Identifier,isReadonly:true, TypeSymbol.Int);
            var body = BindStatement(syntax.Body);

            _scope = _scope.Parent;

            return new BoundForStatement(variable, lowerBound, upperBound, body);
        }

        private BoundExpression BindExpression(ExpressionSyntax syntax, TypeSymbol targetType)
        {
            return BindConversion(syntax, targetType);
        }

        private BoundExpression BindExpression(ExpressionSyntax syntax, bool canBeVoid = false)
        {
            var result = BindExpressionInternal(syntax);
            if (!canBeVoid && result.Type == TypeSymbol.Void)
            {
                _diagnostics.ReportExpressionMustHaveValue(syntax.Span);
                return new BoundErrorExpression();
            }

            return result;
        }

        private BoundExpression BindExpressionInternal(ExpressionSyntax syntax)
        {
            switch(syntax.Kind)
            {
                case SyntaxKind.ParenthesizedExpression:
                    return BindParenthesizedExpression((ParenthesizedExpressionSyntax)syntax);
                case SyntaxKind.LiteralExpression:
                    return BindLiteralExpression((LiteralExpressionSyntax)syntax);
                case SyntaxKind.NameExpression:
                    return BindNameExpression((NameExpressionSyntax)syntax);
                case SyntaxKind.AssignmentExpression:
                    return BindAssignmentExpression((AssignmentExpressionSyntax)syntax);
                case SyntaxKind.UnaryExpression:
                    return BindUnaryExpression((UnaryExpressionSyntax)syntax);
                case SyntaxKind.BinaryExpression:
                    return BindBinaryExpression((BinaryExpressionSyntax)syntax);
                case SyntaxKind.CallExpression:
                    return BindCallExpression((CallExpressionSyntax)syntax);
                default:
                    throw new Exception($"Unexpexted Syntax {syntax.Kind}");
            }
        }

        private BoundExpression BindCallExpression(CallExpressionSyntax syntax)
        {
            if (syntax.Arguments.Count == 1 && LookUpType(syntax.Identifier.Text) is TypeSymbol type)
                return BindConversion(syntax.Arguments[0], type);

            var boundArguments = ImmutableArray.CreateBuilder<BoundExpression>();

            foreach (var argument in syntax.Arguments)
            {
                var boundArgument = BindExpression(argument);
                boundArguments.Add(boundArgument);
            }

            if (!_scope.TryLookUpFunction(syntax.Identifier.Text,out var function))
            {
                _diagnostics.ReportUndefinedFunction(syntax.Identifier.Span, syntax.Identifier.Text);
                return new BoundErrorExpression();
            }
            if (syntax.Arguments.Count != function.Parameter.Length)
            {
                _diagnostics.ReportWrongArgumentCount(syntax.Span, function.Name, function.Parameter.Length, syntax.Arguments.Count);
                return new BoundErrorExpression();
            }
            for (var i = 0; i < syntax.Arguments.Count; i++)
            {
                var argument = boundArguments[i];
                var parameter = function.Parameter[i];

                //fix cascading errors
                if (argument.Type == TypeSymbol.Error)
                    return new BoundErrorExpression();

                if (argument.Type != parameter.Type)
                {
                    _diagnostics.ReportWrongArgumentType(syntax.Span, parameter.Name, parameter.Type, argument.Type);
                    return new BoundErrorExpression();
                }
            }

            return new BoundCallExpression(function, boundArguments.ToImmutable());
        }


        private BoundExpression BindConversion(ExpressionSyntax syntax, TypeSymbol type)
        {
            var expression = BindExpression(syntax);
            return BindConversion(syntax.Span, expression, type);
        }

        private BoundExpression BindConversion(TextSpan diagnosticSpan, BoundExpression expression, TypeSymbol type)
        {
            var conversion = Conversion.Classify(expression.Type, type);

            if (!conversion.Exists)
            {
                if (expression.Type == TypeSymbol.Error && type != TypeSymbol.Error)
                {
                    _diagnostics.ReportCannotConvert(diagnosticSpan, expression.Type, type);
                }
                return new BoundErrorExpression();
            }

            if (conversion.IsIdentity)
                return expression;

            return new BoundConversionExpression(type, expression);
        }

        private BoundExpression BindParenthesizedExpression(ParenthesizedExpressionSyntax syntax)
        {
            return BindExpression(syntax.Expression);
        }



        private BoundExpression BindLiteralExpression(LiteralExpressionSyntax syntax)
        {
            var value = syntax.Value ?? 0;
            return new BoundLiteralExpression(value);
        }
        private BoundExpression BindNameExpression(NameExpressionSyntax syntax)
        {
            var name = syntax.IdentifierToken.Text;
            if (syntax.IdentifierToken.IsMissing)
            {
                return new BoundErrorExpression();
            }

            if (!_scope.TryLookUpVariable(name, out var variable))
            {
                _diagnostics.ReportUndefinedName(syntax.IdentifierToken.Span,name);
                return new BoundErrorExpression();
            }
            return new BoundVariableExpression(variable);
        }

        private BoundExpression BindAssignmentExpression(AssignmentExpressionSyntax syntax)
        {
            var name = syntax.IdentifierToken.Text;
            var boundExpression = BindExpression(syntax.Expression);

            if(!_scope.TryLookUpVariable(name,out var variable))
            {
                _diagnostics.ReportUndefinedName(syntax.IdentifierToken.Span, name);
                return boundExpression;
            }

            if (variable.IsReadonly)
            {
                _diagnostics.ReportCannotAssign(syntax.EqualsToken.Span, name);
            }
            var convertedExpression = BindConversion(syntax.Expression.Span, boundExpression, variable.Type);

            return new BoundAssignmentExpression(variable, convertedExpression);
        }

        private BoundExpression BindUnaryExpression(UnaryExpressionSyntax syntax)
        {
            var boundOperand = BindExpression(syntax.Operand);
            var boundOperator = BoundUnaryOperator.Bind(syntax.OperatorToken.Kind, boundOperand.Type);
            if (boundOperand.Type == TypeSymbol.Error)
                return new BoundErrorExpression();

            if (boundOperator == null)
            {
                _diagnostics.ReportUndefinedUnaryOperator(syntax.OperatorToken.Span, syntax.OperatorToken.Text, boundOperand.Type);
                return new BoundErrorExpression();
            }

            return new BoundUnaryExpression(boundOperator, boundOperand);
        }


        private BoundExpression BindBinaryExpression(BinaryExpressionSyntax syntax)
        {
            var boundLeft = BindExpression(syntax.Left);
            var boundRight = BindExpression(syntax.Right);
            var boundOperator = BoundBinaryOperator.Bind(syntax.OperatorToken.Kind,boundLeft.Type,boundRight.Type);
            
            if(boundLeft.Type==TypeSymbol.Error||boundRight.Type==TypeSymbol.Error)
                return new BoundErrorExpression();

            if (boundOperator == null)
            {
                _diagnostics.ReportUndefinedBinaryOperator(syntax.OperatorToken.Span,syntax.OperatorToken.Text,boundLeft.Type,boundRight.Type);
                return new BoundErrorExpression();
            }

            return new BoundBinaryExpression(boundLeft,boundOperator,  boundRight);
        }

        private VariableSymbol BindVariable(SyntaxToken identifier,bool isReadonly, TypeSymbol type)
        {
            var name = identifier.Text??"?";
            var declare = !identifier.IsMissing;
            var variable = new VariableSymbol(name, isReadonly, type);
            if (declare && !_scope.TryDeclareVariable(variable))
                _diagnostics.ReportVariableAlreadyDeclared(identifier.Span, name);
            return variable;
        }

        private TypeSymbol LookUpType(string name)
        {
            switch (name)
            {
                case "bool":
                    return TypeSymbol.Bool;
                case "int":
                    return TypeSymbol.Int;
                case "string":
                    return TypeSymbol.String;
                default:
                    return null;
            }
        }

    }
}
