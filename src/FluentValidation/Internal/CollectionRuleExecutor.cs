#region License

// Copyright (c) .NET Foundation and contributors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// The latest version of this file can be found at https://github.com/FluentValidation/FluentValidation

#endregion

namespace FluentValidation.Internal {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Threading;
	using System.Threading.Tasks;
	using Results;
	using Validators;

	internal class CollectionRuleExecutor<T, TElement, TValue> : RuleExecutor<T, IEnumerable<TElement>, TValue> {

		private readonly CollectionPropertyRule<T, TElement> _rule;
		private readonly Func<T, TElement, TValue> _elementAccessor;

		public CollectionRuleExecutor(CollectionPropertyRule<T, TElement> rule, Func<T, TElement, TValue> elementAccessor)
			: base(rule, null) {
			_rule = rule;
			_elementAccessor = elementAccessor;
		}

		private async Task<IEnumerable<ValidationFailure>> InvokePropertyValidatorAsync(IValidationContext<T> context, IPropertyValidator validator, string propertyName, TValue value, int index, CancellationToken cancellation) {
			var newPropertyContext = new PropertyValidatorContext(context, _rule, propertyName, value);
			newPropertyContext.MessageFormatter.AppendArgument("CollectionIndex", index);
			return await validator.ValidateAsync(newPropertyContext, cancellation);
		}

		private IEnumerable<Results.ValidationFailure> InvokePropertyValidator(IValidationContext<T> context, IPropertyValidator validator, string propertyName, TValue value, int index) {
			var newPropertyContext = new PropertyValidatorContext(context, _rule, propertyName, value);
			newPropertyContext.MessageFormatter.AppendArgument("CollectionIndex", index);
			return validator.Validate(newPropertyContext);
		}

		public sealed override IEnumerable<ValidationFailure> Validate(IValidationContext<T> context) {
			string displayName = _rule.GetDisplayName(context);

			if (_rule.PropertyName == null && displayName == null) {
				//No name has been specified. Assume this is a model-level rule, so we should use empty string instead.
				displayName = string.Empty;
			}

			// Construct the full name of the property, taking into account overriden property names and the chain (if we're in a nested validator)
			string propertyName = context.PropertyChain.BuildPropertyName(_rule.PropertyName ?? displayName);

			if (string.IsNullOrEmpty(propertyName)) {
				propertyName = InferPropertyName(_rule.Expression);
			}

			// Ensure that this rule is allowed to run.
			// The validatselector has the opportunity to veto this before any of the validators execute.
			if (!context.Selector.CanExecute(_rule, propertyName, context)) {
				return Enumerable.Empty<ValidationFailure>();
			}

			if (_rule.HasCondition) {
				if (!_rule.Condition(context)) {
					return Enumerable.Empty<ValidationFailure>();
				}
			}

			// TODO: For FV 9, throw an exception by default if synchronous validator has async condition.
			if (_rule.HasAsyncCondition) {
				if (! _rule.AsyncCondition(context, default).GetAwaiter().GetResult()) {
					return Enumerable.Empty<ValidationFailure>();
				}
			}

			var filteredValidators = GetValidatorsToExecute(context);

			if (filteredValidators.Count == 0) {
				// If there are no property validators to execute after running the conditions, bail out.
				return Enumerable.Empty<ValidationFailure>();
			}

			var cascade = _rule.CascadeMode;
			var failures = new List<ValidationFailure>();
			var collection = _rule.PropertyFunc(context.InstanceToValidate);

			int count = 0;

			if (collection != null) {
				if (string.IsNullOrEmpty(propertyName)) {
					throw new InvalidOperationException("Could not automatically determine the property name ");
				}

				var actualContext = ValidationContext<T>.GetFromNonGenericContext(context);

				foreach (var element in collection) {
					int index = count++;

					if (_rule.Filter != null && !_rule.Filter(element)) {
						continue;
					}

					string indexer = index.ToString();
					bool useDefaultIndexFormat = true;

					if (_rule.IndexBuilder != null) {
						indexer = _rule.IndexBuilder(context.InstanceToValidate, collection, element, index);
						useDefaultIndexFormat = false;
					}

					ValidationContext<T> newContext = actualContext.CloneForChildCollectionValidator(actualContext.InstanceToValidate, preserveParentContext: true);
					newContext.PropertyChain.Add(propertyName);
					newContext.PropertyChain.AddIndexer(indexer, useDefaultIndexFormat);

					var valueToValidate = _elementAccessor(context.InstanceToValidate, element);
					var propertyNameToValidate = newContext.PropertyChain.ToString();

					foreach (var validator in filteredValidators) {
						if (validator.ShouldValidateAsynchronously(context)) {
							failures.AddRange(InvokePropertyValidatorAsync(newContext, validator, propertyNameToValidate, valueToValidate, index, default).GetAwaiter().GetResult());
						}
						else {
							failures.AddRange(InvokePropertyValidator(newContext, validator, propertyNameToValidate, valueToValidate, index));
						}

						// If there has been at least one failure, and our CascadeMode has been set to StopOnFirst
						// then don't continue to the next rule
#pragma warning disable 618
						if (failures.Count > 0 && (cascade == CascadeMode.StopOnFirstFailure || cascade == CascadeMode.Stop)) {
							goto AfterValidate; // 🙃
						}
#pragma warning restore 618
					}
				}
			}

			AfterValidate:

			if (failures.Count > 0) {
				// Callback if there has been at least one property validator failed.
				_rule.OnFailure?.Invoke(context.InstanceToValidate, failures);
			}
			else {
				foreach (var dependentRule in _rule.DependentRules) {
					failures.AddRange(dependentRule.Validate(context));
				}
			}

			return failures;
		}

		public sealed override async Task<IEnumerable<ValidationFailure>> ValidateAsync(IValidationContext<T> context, CancellationToken cancellation) {
			if (!context.IsAsync()) {
				context.RootContextData["__FV_IsAsyncExecution"] = true;
			}

			string displayName = _rule.GetDisplayName(context);

			if (_rule.PropertyName == null && displayName == null) {
				//No name has been specified. Assume this is a model-level rule, so we should use empty string instead.
				displayName = string.Empty;
			}

			// Construct the full name of the property, taking into account overriden property names and the chain (if we're in a nested validator)
			string propertyName = context.PropertyChain.BuildPropertyName(_rule.PropertyName ?? displayName);

			if (string.IsNullOrEmpty(propertyName)) {
				propertyName = InferPropertyName(_rule.Expression);
			}

			// Ensure that this rule is allowed to run.
			// The validatselector has the opportunity to veto this before any of the validators execute.
			if (!context.Selector.CanExecute(_rule, propertyName, context)) {
				return Enumerable.Empty<ValidationFailure>();
			}

			if (_rule.HasCondition) {
				if (!_rule.Condition(context)) {
					return Enumerable.Empty<ValidationFailure>();
				}
			}

			// TODO: For FV 9, throw an exception by default if synchronous validator has async condition.
			if (_rule.HasAsyncCondition) {
				if (! _rule.AsyncCondition(context, default).GetAwaiter().GetResult()) {
					return Enumerable.Empty<ValidationFailure>();
				}
			}

			var filteredValidators = await GetValidatorsToExecuteAsync(context, cancellation);

			if (filteredValidators.Count == 0) {
				// If there are no property validators to execute after running the conditions, bail out.
				return Enumerable.Empty<ValidationFailure>();
			}

			var cascade = _rule.CascadeMode;
			var failures = new List<ValidationFailure>();
			var collection = _rule.PropertyFunc(context.InstanceToValidate);

			int count = 0;

			if (collection != null) {
				if (string.IsNullOrEmpty(propertyName)) {
					throw new InvalidOperationException("Could not automatically determine the property name ");
				}

				var actualContext = ValidationContext<T>.GetFromNonGenericContext(context);

				foreach (var element in collection) {
					int index = count++;

					if (_rule.Filter != null && !_rule.Filter(element)) {
						continue;
					}

					string indexer = index.ToString();
					bool useDefaultIndexFormat = true;

					if (_rule.IndexBuilder != null) {
						indexer = _rule.IndexBuilder(context.InstanceToValidate, collection, element, index);
						useDefaultIndexFormat = false;
					}

					ValidationContext<T> newContext = actualContext.CloneForChildCollectionValidator(actualContext.InstanceToValidate, preserveParentContext: true);
					newContext.PropertyChain.Add(propertyName);
					newContext.PropertyChain.AddIndexer(indexer, useDefaultIndexFormat);

					var valueToValidate = _elementAccessor(context.InstanceToValidate, element);
					var propertyNameToValidate = newContext.PropertyChain.ToString();


					foreach (var validator in filteredValidators) {
						if (validator.ShouldValidateAsynchronously(context)) {
							failures.AddRange(await InvokePropertyValidatorAsync(newContext, validator, propertyNameToValidate, valueToValidate, index, cancellation));
						}
						else {
							failures.AddRange(InvokePropertyValidator(newContext, validator, propertyNameToValidate, valueToValidate, index));
						}

						// If there has been at least one failure, and our CascadeMode has been set to StopOnFirst
						// then don't continue to the next rule
#pragma warning disable 618
						if (failures.Count > 0 && (cascade == CascadeMode.StopOnFirstFailure || cascade == CascadeMode.Stop)) {
							goto AfterValidate; // 🙃
						}
#pragma warning restore 618
					}
				}
			}

			AfterValidate:

			if (failures.Count > 0) {
				// Callback if there has been at least one property validator failed.
				_rule.OnFailure?.Invoke(context.InstanceToValidate, failures);
			}
			else {
				foreach (var dependentRule in _rule.DependentRules) {
					cancellation.ThrowIfCancellationRequested();
					failures.AddRange(await dependentRule.ValidateAsync(context, cancellation));
				}
			}

			return failures;
		}

		private List<IPropertyValidator> GetValidatorsToExecute(IValidationContext<T> context) {
			// Loop over each validator and check if its condition allows it to run.
			// This needs to be done prior to the main loop as within a collection rule
			// validators' conditions still act upon the root object, not upon the collection property.
			// This allows the property validators to cancel their execution prior to the collection
			// being retrieved (thereby possibly avoiding NullReferenceExceptions).
			// Must call ToList so we don't modify the original collection mid-loop.
			var validators = _rule.Validators.ToList();
			int validatorIndex = 0;
			foreach (var validator in _rule.Validators) {
				if (validator.Options.HasCondition) {
					if (!validator.Options.InvokeCondition(context)) {
						validators.RemoveAt(validatorIndex);
					}
				}

				if (validator.Options.HasAsyncCondition) {
					if (!validator.Options.InvokeAsyncCondition(context, default).GetAwaiter().GetResult()) {
						validators.RemoveAt(validatorIndex);
					}
				}

				validatorIndex++;
			}

			return validators;
		}

		private async Task<List<IPropertyValidator>> GetValidatorsToExecuteAsync(IValidationContext<T> context, CancellationToken cancellation) {
			// Loop over each validator and check if its condition allows it to run.
			// This needs to be done prior to the main loop as within a collection rule
			// validators' conditions still act upon the root object, not upon the collection property.
			// This allows the property validators to cancel their execution prior to the collection
			// being retrieved (thereby possibly avoiding NullReferenceExceptions).
			// Must call ToList so we don't modify the original collection mid-loop.
			var validators = _rule.Validators.ToList();
			int validatorIndex = 0;
			foreach (var validator in _rule.Validators) {
				if (validator.Options.HasCondition) {
					if (!validator.Options.InvokeCondition(context)) {
						validators.RemoveAt(validatorIndex);
					}
				}

				if (validator.Options.HasAsyncCondition) {
					if (!await validator.Options.InvokeAsyncCondition(context, cancellation)) {
						validators.RemoveAt(validatorIndex);
					}
				}

				validatorIndex++;
			}

			return validators;
		}


		private static string InferPropertyName(LambdaExpression expression) {
			var paramExp = expression.Body as ParameterExpression;

			if (paramExp == null) {
				throw new InvalidOperationException("Could not infer property name for expression: " + expression + ". Please explicitly specify a property name by calling OverridePropertyName as part of the rule chain. Eg: RuleForEach(x => x).NotNull().OverridePropertyName(\"MyProperty\")");
			}

			return paramExp.Name;
		}
	}
}