﻿using System.ComponentModel;
using System.Linq;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;

public partial class ModuleWeaver
{
    public EventInvokerMethod AddOnPropertyChangedMethod(TypeDefinition targetType)
    {
        if (FoundInterceptor)
        {
            if (targetType.HasGenericParameters)
            {
                var message = $"Error processing '{targetType.Name}'. Interception is not supported on generic types. To manually work around this problem add a [DoNotNotify] to the class and then manually implement INotifyPropertyChanged for that class and all child classes. If you would like this feature handled automatically feel free to submit a pull request.";
                throw new WeavingException(message);
            }

            var methodDefinition = GetMethodDefinition(targetType, out _);

            return new EventInvokerMethod
            {
                MethodReference = InjectInterceptedMethod(targetType, methodDefinition).GetGeneric(),
                InvokerType = InterceptorType,
                IsVisibleFromChildren = true
            };
        }

        return new EventInvokerMethod
        {
            MethodReference = InjectMethod(targetType, EventInvokerNames.First(), out var invokerType).GetGeneric(),
            InvokerType = invokerType,
            IsVisibleFromChildren = true
        };
    }

    MethodDefinition GetMethodDefinition(TypeDefinition targetType, out InvokerTypes invokerType)
    {
        var eventInvokerName = $"Inner{EventInvokerNames.First()}";
        var methodDefinition = targetType.Methods.FirstOrDefault(x => x.Name == eventInvokerName);
        if (methodDefinition?.Parameters.Count == 1 && methodDefinition.Parameters[0].ParameterType.FullName == "System.String")
        {
            invokerType = InvokerTypes.String;
            return methodDefinition;
        }

        return InjectMethod(targetType, eventInvokerName, out invokerType);
    }

    public FieldDefinition GetEventHandlerField(TypeDefinition targetType)
    {
        var addMethod = targetType.Methods.SingleOrDefault(i => i.Overrides.Any(o => o.FullName == "System.Void System.ComponentModel.INotifyPropertyChanged::add_PropertyChanged(System.ComponentModel.PropertyChangedEventHandler)"))
                        ?? targetType.Events.SingleOrDefault(i => i.Name == nameof(INotifyPropertyChanged.PropertyChanged))?.AddMethod;

        if (addMethod == null)
            return null;

        var fieldReferences = addMethod.Body.Instructions
                                       .Where(i => i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Ldflda || i.OpCode == OpCodes.Stfld)
                                       .Select(i => i.Operand)
                                       .OfType<FieldReference>()
                                       .Where(fld => IsPropertyChangedEventHandler(fld.FieldType) || IsFsharpEventHandler(fld.FieldType))
                                       .ToList();

        if (fieldReferences.Select(i => i.FullName).Distinct().Count() != 1)
            return null;

        return fieldReferences[0].Resolve();
    }

    MethodDefinition InjectMethod(TypeDefinition targetType, string eventInvokerName, out InvokerTypes invokerType)
    {
        var propertyChangedFieldDef = GetEventHandlerField(targetType);
        if (propertyChangedFieldDef != null)
        {
            var propertyChangedField = propertyChangedFieldDef.GetGeneric();

            if (IsFsharpEventHandler(propertyChangedFieldDef.FieldType))
            {
                invokerType = InvokerTypes.String;
                return InjectFsharp(targetType, eventInvokerName, propertyChangedFieldDef);
            }

            if (FoundInterceptor)
            {
                invokerType = InvokerTypes.String;
                return InjectNormal(targetType, eventInvokerName, propertyChangedField);
            }

            invokerType = InvokerTypes.PropertyChangedArg;
            return InjectEventArgsMethod(targetType, eventInvokerName, propertyChangedField);
        }

        var message = $"Could not inject EventInvoker method on type '{targetType.FullName}'. It is possible you are inheriting from a base class and have not correctly set 'EventInvokerNames' or you are using a explicit PropertyChanged event and the event field is not visible to this instance. Either correct 'EventInvokerNames' or implement your own EventInvoker on this class. If you want to suppress this place a [DoNotNotifyAttribute] on {targetType.FullName}.";
        throw new WeavingException(message);
    }

    MethodDefinition InjectFsharp(TypeDefinition targetType, string eventInvokerName, FieldDefinition fsharpEvent)
    {
        var method = new MethodDefinition(eventInvokerName, GetMethodAttributes(targetType), ModuleDefinition.TypeSystem.Void);
        method.Parameters.Add(new ParameterDefinition("propertyName", ParameterAttributes.None, ModuleDefinition.TypeSystem.String));

        var instructions = method.Body.Instructions;
        instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        instructions.Add(Instruction.Create(OpCodes.Ldfld, fsharpEvent));
        instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        instructions.Add(Instruction.Create(OpCodes.Newobj, PropertyChangedEventConstructorReference));
        instructions.Add(Instruction.Create(OpCodes.Tail));
        instructions.Add(Instruction.Create(OpCodes.Callvirt, Trigger.Value));
        instructions.Add(Instruction.Create(OpCodes.Ret));
        method.Body.InitLocals = true;
        targetType.Methods.Add(method);
        return method;
    }

    MethodDefinition InjectNormal(TypeDefinition targetType, string eventInvokerName, FieldReference propertyChangedField)
    {
        var method = new MethodDefinition(eventInvokerName, GetMethodAttributes(targetType), ModuleDefinition.TypeSystem.Void);
        method.Parameters.Add(new ParameterDefinition("propertyName", ParameterAttributes.None, ModuleDefinition.TypeSystem.String));

        var handlerVariable = new VariableDefinition(PropChangedHandlerReference);
        method.Body.Variables.Add(handlerVariable);

        var instructions = method.Body.Instructions;

        var last = Instruction.Create(OpCodes.Ret);
        instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        instructions.Add(Instruction.Create(OpCodes.Ldfld, propertyChangedField));
        instructions.Add(Instruction.Create(OpCodes.Stloc_0));
        instructions.Add(Instruction.Create(OpCodes.Ldloc_0));
        instructions.Add(Instruction.Create(OpCodes.Brfalse_S, last));
        instructions.Add(Instruction.Create(OpCodes.Ldloc_0));
        instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        instructions.Add(Instruction.Create(OpCodes.Newobj, PropertyChangedEventConstructorReference));
        instructions.Add(Instruction.Create(OpCodes.Tail));
        instructions.Add(Instruction.Create(OpCodes.Callvirt, PropertyChangedEventHandlerInvokeReference));

        instructions.Add(last);
        method.Body.InitLocals = true;
        targetType.Methods.Add(method);
        return method;
    }

    MethodDefinition InjectEventArgsMethod(TypeDefinition targetType, string eventInvokerName, FieldReference propertyChangedField)
    {
        var method = new MethodDefinition(eventInvokerName, GetMethodAttributes(targetType), ModuleDefinition.TypeSystem.Void);
        method.Parameters.Add(new ParameterDefinition("eventArgs", ParameterAttributes.None, PropertyChangedEventArgsReference));

        var handlerVariable = new VariableDefinition(PropChangedHandlerReference);
        method.Body.Variables.Add(handlerVariable);

        var instructions = method.Body.Instructions;

        var last = Instruction.Create(OpCodes.Ret);
        instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        instructions.Add(Instruction.Create(OpCodes.Ldfld, propertyChangedField));
        instructions.Add(Instruction.Create(OpCodes.Stloc_0));
        instructions.Add(Instruction.Create(OpCodes.Ldloc_0));
        instructions.Add(Instruction.Create(OpCodes.Brfalse_S, last));
        instructions.Add(Instruction.Create(OpCodes.Ldloc_0));
        instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        instructions.Add(Instruction.Create(OpCodes.Tail));
        instructions.Add(Instruction.Create(OpCodes.Callvirt, PropertyChangedEventHandlerInvokeReference));

        instructions.Add(last);
        method.Body.InitLocals = true;
        targetType.Methods.Add(method);
        return method;
    }

    static MethodAttributes GetMethodAttributes(TypeDefinition targetType)
    {
        if (targetType.IsSealed)
        {
            return MethodAttributes.Public | MethodAttributes.HideBySig;
        }

        return MethodAttributes.Virtual | MethodAttributes.Public | MethodAttributes.NewSlot;
    }

    static bool IsPropertyChangedEventHandler(TypeReference type)
    {
        return type.FullName == "System.ComponentModel.PropertyChangedEventHandler"
               || type.FullName == "Windows.UI.Xaml.Data.PropertyChangedEventHandler"
               || type.FullName == "System.Runtime.InteropServices.WindowsRuntime.EventRegistrationTokenTable`1<Windows.UI.Xaml.Data.PropertyChangedEventHandler>";
    }

    static bool IsFsharpEventHandler(TypeReference type)
    {
        return type.FullName == "Microsoft.FSharp.Control.FSharpEvent`2<System.ComponentModel.PropertyChangedEventHandler,System.ComponentModel.PropertyChangedEventArgs>";
    }
}
