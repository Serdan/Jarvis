using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.SignalR.Client;
using SignalRClient = Microsoft.AspNetCore.SignalR.Client; // Namespace alias

namespace JarvisClient.Extensions;

public static class HubConnectionExtensions
{
    public static IDisposable On<TClient>(this HubConnection hub, TClient client, Expression<Func<TClient, Delegate>> handler)
    {
        if (handler.Body is not UnaryExpression unaryExpression)
        {
            throw new InvalidOperationException("Invalid expression. UnaryExpression expected.");
        }

        if (unaryExpression.Operand is not MethodCallExpression methodCallExpression)
        {
            throw new InvalidOperationException("Invalid expression. MethodCallExpression expected.");
        }

        if (methodCallExpression.Object is not ConstantExpression constantExpression)
        {
            throw new InvalidOperationException("Invalid expression. ConstantExpression expected.");
        }

        var methodInfo = constantExpression.Value as MethodInfo;
        if (methodInfo == null)
        {
            throw new InvalidOperationException("Invalid expression. MethodInfo expected.");
        }

        var methodName = methodInfo.Name;

        var parameterTypes = methodInfo.GetParameters()
                                       .Select(p => p.ParameterType)
                                       .ToArray();

        var onMethod = typeof(SignalRClient.HubConnectionExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
                                                                    .FirstOrDefault(m => IsCorrectOnMethod(m, parameterTypes.Length, methodInfo.ReturnType));

        if (onMethod is null)
        {
            throw new InvalidOperationException("Method 'On' not found.");
        }

        var delegateType = Expression.GetDelegateType(parameterTypes.Concat([methodInfo.ReturnType]).ToArray());
        var handlerDelegate = methodInfo.CreateDelegate(delegateType, client);

        var genericOnMethod = onMethod.MakeGenericMethod(parameterTypes);

        return (IDisposable) genericOnMethod.Invoke(null, [hub, methodName, handlerDelegate])!;
    }

    private static bool IsCorrectOnMethod(MethodInfo method, int parameterCount, Type returnType)
    {
        if (method.Name != "On")
        {
            return false;
        }

        var parameters = method.GetParameters();

        if (parameters.Length != 3 ||
            parameters[0].ParameterType != typeof(HubConnection) ||
            parameters[1].ParameterType != typeof(string))
        {
            return false;
        }

        var del = parameters[2];
        var delType = del.ParameterType;

        if (returnType == typeof(void))
        {
            if (delType.Name.StartsWith("Action") is false)
            {
                return false;
            }

            return parameterCount == delType.GenericTypeArguments.Length;
        }

        if (returnType == typeof(Task))
        {
            if (delType.Name.StartsWith("Func") is false)
            {
                return false;
            }

            return parameterCount == delType.GenericTypeArguments.Length - 1;
        }

        return false;
    }

    public static Task InvokeAsync<THub>(this HubConnection hub, Expression<Func<THub, Task>> f)
    {
        if (f.Body is not MethodCallExpression methodCall)
        {
            throw new InvalidOperationException("Invalid expression");
        }

        var args = methodCall.Arguments;
        var argValues = new List<object?>(args.Count);

        foreach (var arg in args)
        {
            switch (arg)
            {
                case ConstantExpression constant:
                    argValues.Add(constant.Value);
                    break;
                case MemberExpression member:
                    argValues.Add(GetValue(member));
                    break;
            }
        }

        var name = methodCall.Method.Name;

        return hub.InvokeCoreAsync(name, argValues.ToArray());
    }

    private static object GetValue(MemberExpression member)
    {
        var objectMember = Expression.Convert(member, typeof(object));
        var getterLambda = Expression.Lambda<Func<object>>(objectMember);
        var getter = getterLambda.Compile();
        return getter();
    }
}
