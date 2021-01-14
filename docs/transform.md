# Transforming Values

As of FluentValidation 9.5, you can apply a transformation to a property value prior to validation being performed against it. For example, if you have property of type `string` that actually contains numeric input, you could apply a transformation to convert the string value to a number.


```csharp
RuleFor(x => x.SomeStringProperty, transform: value => int.TryParse(value, out int val) ? (int?) val : null)
    .GreaterThan(10);
```

This rule transforms the value from a `string` to an nullable `int` (returning `null` if the value couldn't be converted). A greater-than check is then performed on the resulting value.

Syntactically this is not particularly nice to read, so the logic for the transform can optionally be moved into a separate method:

```csharp
RuleFor(x => x.SomeStringProperty, transform: StringToNullableInt)
    .GreaterThan(10);

int? StringToNullableInt(string value)
  => int.TryParse(value, out int val) ? (int?) val : null;

```

This syntax is available in FluentValidation 9.5 and newer.


# Transforming Values (9.0 - 9.4)

Prior to FluentValidation 9.5, you can use the `Transform` method to achieve the same result.

```csharp
RuleFor(x => x.SomeStringProperty)
    .Transform(value => int.TryParse(value, out int val) ? (int?) val : null)
    .GreaterThan(10);
```

The `Transform` method is marked as obsolete as of FluentValidation 9.5 and is removed in FluentValidation 10.0. In newer versions of FluentValidation the transformation should be applied as part of call to `RuleFor` (see above).
