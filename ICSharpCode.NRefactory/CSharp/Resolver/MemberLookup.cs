﻿// Copyright (c) 2010 AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under MIT X11 license (for details please see \doc\license.txt)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.NRefactory.TypeSystem;

namespace ICSharpCode.NRefactory.CSharp.Resolver
{
	/// <summary>
	/// Implementation of member lookup (C# 4.0 spec, §7.4).
	/// </summary>
	public class MemberLookup
	{
		#region Static helper methods
		/// <summary>
		/// Gets whether the member is considered to be invocable.
		/// </summary>
		public static bool IsInvocable(IMember member, ITypeResolveContext context)
		{
			if (member == null)
				throw new ArgumentNullException("member");
			// C# 4.0 spec, §7.4 member lookup
			if (member is IEvent || member is IMethod)
				return true;
			if (member.ReturnType == SharedTypes.Dynamic)
				return true;
			return member.ReturnType.Resolve(context).IsDelegate();
		}
		#endregion
		
		ITypeResolveContext context;
		ITypeDefinition currentTypeDefinition;
		IProjectContent currentProject;
		
		public MemberLookup(ITypeResolveContext context, ITypeDefinition currentTypeDefinition, IProjectContent currentProject)
		{
			if (context == null)
				throw new ArgumentNullException("context");
			this.context = context;
			this.currentTypeDefinition = currentTypeDefinition;
			this.currentProject = currentProject;
		}
		
		#region IsAccessible
		/// <summary>
		/// Gets whether <paramref name="entity"/> is accessible in the current class.
		/// </summary>
		/// <param name="member">The entity to test</param>
		/// <param name="allowProtectedAccess">Whether protected access is allowed.
		/// True if the type of the reference is derived from the current class.</returns>
		public bool IsAccessible(IEntity entity, bool allowProtectedAccess)
		{
			if (entity == null)
				throw new ArgumentNullException("entity");
			// C# 4.0 spec, §3.5.2 Accessiblity domains
			switch (entity.Accessibility) {
				case Accessibility.None:
					return false;
				case Accessibility.Private:
					return entity.DeclaringTypeDefinition == currentTypeDefinition;
				case Accessibility.Public:
					return true;
				case Accessibility.Protected:
					return allowProtectedAccess && IsProtectedAccessible(entity.DeclaringTypeDefinition);
				case Accessibility.Internal:
					return IsInternalAccessible(entity.ProjectContent);
				case Accessibility.ProtectedOrInternal:
					return (allowProtectedAccess && IsProtectedAccessible(entity.DeclaringTypeDefinition))
						|| IsInternalAccessible(entity.ProjectContent);
				case Accessibility.ProtectedAndInternal:
					return (allowProtectedAccess && IsProtectedAccessible(entity.DeclaringTypeDefinition))
						&& IsInternalAccessible(entity.ProjectContent);
				default:
					throw new Exception("Invalid value for Accessibility");
			}
		}
		
		bool IsInternalAccessible(IProjectContent declaringProject)
		{
			return declaringProject != null && currentProject != null && declaringProject.InternalsVisibleTo(currentProject, context);
		}
		
		bool IsProtectedAccessible(ITypeDefinition declaringType)
		{
			if (declaringType == currentTypeDefinition)
				return true;
			// PERF: this might hurt performance as this method is called several times (once for each member)
			// make sure resolving base types is cheap (caches?) or cache within the MemberLookup instance
			return currentTypeDefinition != null && currentTypeDefinition.IsDerivedFrom(declaringType, context);
		}
		#endregion
		
		/// <summary>
		/// Performs a member lookup.
		/// </summary>
		public ResolveResult Lookup(IType type, string name, int typeParameterCount, bool isInvocation)
		{
			List<IType> types = new List<IType>();
			List<IMember> members = new List<IMember>();
			if (!isInvocation) {
				// Consider nested types only if it's not an invocation. The type parameter count must match in this case.
				types.AddRange(type.GetNestedTypes(context,
				                                   d => d.TypeParameterCount == typeParameterCount
				                                   && d.Name == name && IsAccessible(d, true)));
			}
			
			ITypeDefinition typeDef = type.GetDefinition();
			bool allowProtectedAccess = typeDef != null && typeDef.IsDerivedFrom(currentTypeDefinition, context);
			
			if (typeParameterCount == 0) {
				Predicate<IMember> memberFilter = delegate(IMember member) {
					return !member.IsOverride && member.Name == name && IsAccessible(member, allowProtectedAccess);
				};
				members.AddRange(type.GetMethods(context, memberFilter));
				members.AddRange(type.GetProperties(context, memberFilter));
				members.AddRange(type.GetFields(context, memberFilter));
				members.AddRange(type.GetEvents(context, memberFilter));
				if (isInvocation)
					members.RemoveAll(m => IsInvocable(m, context));
			} else {
				// No need to check for isInvocation/isInvocable here:
				// we filter out all non-methods
				Predicate<IMethod> memberFilter = delegate(IMethod method) {
					return method.TypeParameters.Count == typeParameterCount
						&& !method.IsOverride && method.Name == name && IsAccessible(method, allowProtectedAccess);
				};
				members.AddRange(type.GetMethods(context, memberFilter));
			}
			
			// remove types hidden by other types
			for (int i = types.Count - 1; i >= 0; i--) {
				ITypeDefinition d = GetDeclaringTypeDef(types[i]);
				if (d == null)
					continue;
				// nested loop depends on the fact that the members of more derived classes appear later in the list
				for (int j = i + 1; j < types.Count; j++) {
					if (types[i].TypeParameterCount != types[j].TypeParameterCount)
						continue;
					ITypeDefinition s = GetDeclaringTypeDef(types[j]);
					if (s != null && s != d && s.IsDerivedFrom(d, context)) {
						// types[j] hides types[i]
						types.RemoveAt(i);
						break;
					}
				}
			}
			// remove members hidden by types
			for (int i = 0; i < types.Count; i++) {
				ITypeDefinition d = GetDeclaringTypeDef(types[i]);
				if (d != null)
					members.RemoveAll(m => d.IsDerivedFrom(m.DeclaringTypeDefinition, context));
			}
			// remove members hidden by other members
			for (int i = members.Count - 1; i >= 0; i--) {
				ITypeDefinition d = members[i].DeclaringTypeDefinition;
				IMethod mi = members[i] as IMethod;
				// nested loop depends on the fact that the members of more derived classes appear later in the list
				for (int j = i + 1; j < members.Count; j++) {
					if (mi != null) {
						IMethod mj = members[j] as IMethod;
						if (mj != null && !ParameterListComparer.Instance.Equals(mi, mj))
							continue;
					}
					ITypeDefinition s = members[j].DeclaringTypeDefinition;
					if (s != null && s != d && s.IsDerivedFrom(d, context)) {
						// members[j] hides members[i]
						members.RemoveAt(i);
						break;
					}
				}
			}
			// remove interface members hidden by class members
			if (type is ITypeParameter) {
				// this can happen only with type parameters
				for (int i = members.Count - 1; i >= 0; i--) {
					ITypeDefinition d = members[i].DeclaringTypeDefinition;
					if (d.ClassType != ClassType.Interface)
						continue;
					IMethod mi = members[i] as IMethod;
					for (int j = 0; j < members.Count; j++) {
						if (mi != null) {
							IMethod mj = members[j] as IMethod;
							if (mj != null && !ParameterListComparer.Instance.Equals(mi, mj))
								continue;
						}
						ITypeDefinition s = members[j].DeclaringTypeDefinition;
						if (s != null && IsNonInterfaceType(s)) {
							// members[j] hides members[i]
							members.RemoveAt(i);
							break;
						}
					}
				}
			}
			
			if (types.Count == 1 && members.Count == 0)
				return new TypeResolveResult(types[0]);
			if (types.Count > 0)
				return new AmbiguousTypeResolveResult(types[0]);
			IMember firstNonMethod = members.FirstOrDefault(m => !(m is IMethod));
			if (members.Count == 1 && firstNonMethod != null)
				return new MemberResolveResult(firstNonMethod, firstNonMethod.ReturnType.Resolve(context));
			if (firstNonMethod == null)
				return new MethodGroupResolveResult(members.ConvertAll(m => (IMethod)m));
			return new AmbiguousMemberResultResult(firstNonMethod, firstNonMethod.ReturnType.Resolve(context));
		}
		
		static bool IsNonInterfaceType(ITypeDefinition def)
		{
			return def.ClassType != ClassType.Interface && !(def.Name == "Object" && def.Namespace == "System" && def.TypeParameterCount == 0);
		}
		
		static ITypeDefinition GetDeclaringTypeDef(IType type)
		{
			IType declType = type.DeclaringType;
			return declType != null ? declType.GetDefinition() : null;
		}
	}
}
