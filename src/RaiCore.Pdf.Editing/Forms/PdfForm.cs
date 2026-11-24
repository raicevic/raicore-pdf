namespace RaiCore.Pdf.Editing;

/// <summary>Whether a document has an XFA form (XML Forms Architecture), and which kind.</summary>
public enum PdfXfaKind
{
    /// <summary>No XFA form.</summary>
    None,

    /// <summary>
    /// A static XFA form: the pages are ordinary PDF pages, and the AcroForm fields that come with it are what viewers
    /// without XFA show and fill.
    /// </summary>
    Static,

    /// <summary>
    /// A dynamic XFA form (<c>/NeedsRendering true</c>): the pages are laid out from the XFA data by the viewer; the
    /// PDF pages are usually only a "please wait" placeholder.
    /// </summary>
    Dynamic,
}

/// <summary>The kind of a form field (ISO 32000-2, 12.7.5).</summary>
public enum PdfFieldType
{
    /// <summary>A text field.</summary>
    Text,

    /// <summary>A check box.</summary>
    CheckBox,

    /// <summary>A group of radio buttons: one field, one widget per button.</summary>
    RadioButton,

    /// <summary>A push button, which has no value.</summary>
    PushButton,

    /// <summary>A list box.</summary>
    ListBox,

    /// <summary>A combo box (a drop-down list, possibly editable).</summary>
    ComboBox,

    /// <summary>A signature field.</summary>
    Signature,
}

/// <summary>The form of a document, read by <see cref="PdfEditor.GetForm"/>: its fields, and whether it has XFA.</summary>
public sealed class PdfForm
{
    internal PdfForm(bool hasAcroForm, PdfXfaKind xfa, IReadOnlyList<PdfFormField> fields)
    {
        HasAcroForm = hasAcroForm;
        Xfa = xfa;
        Fields = fields;
    }

    /// <summary>Whether the document has an interactive form (AcroForm), even one without fields.</summary>
    public bool HasAcroForm { get; }

    /// <summary>Whether the document has an XFA form, static or dynamic.</summary>
    public PdfXfaKind Xfa { get; }

    /// <summary>
    /// Whether the document's PDF pages are only a placeholder for a dynamic XFA form: the form is dynamic and the
    /// AcroForm behind it has no fields, so the pages hold nothing but a "please wait" message. Removing the XFA
    /// (<see cref="PdfEditor.RemoveXfa"/>) would leave only that message; the form's content is in the XFA alone.
    /// </summary>
    public bool IsXfaPlaceholder => Xfa == PdfXfaKind.Dynamic && Fields.Count == 0;

    /// <summary>
    /// The fields, in the order of the form's field tree: terminal fields only (those that have a value), with their full
    /// names. Fields whose widgets are all on deleted pages are not listed.
    /// </summary>
    public IReadOnlyList<PdfFormField> Fields { get; }

    /// <summary>The field with a fully qualified name, or <c>null</c>.</summary>
    public PdfFormField? this[string fullName] => Fields.FirstOrDefault(f => f.FullName == fullName);
}

/// <summary>A form field: its name, type, value, flags and widgets.</summary>
public sealed class PdfFormField
{
    internal PdfFormField(
        string fullName, PdfFieldType type, IReadOnlyList<string> values, IReadOnlyList<string> options, bool isReadOnly, bool isRequired,
        IReadOnlyList<PdfFormWidget> widgets)
    {
        FullName = fullName;
        Type = type;
        Values = values;
        Options = options;
        IsReadOnly = isReadOnly;
        IsRequired = isRequired;
        Widgets = widgets;
    }

    /// <summary>The fully qualified name: the partial names of the field and its ancestors, joined by periods (<c>address.city</c>).</summary>
    public string FullName { get; }

    /// <summary>The field's type.</summary>
    public PdfFieldType Type { get; }

    /// <summary>
    /// The value (<c>/V</c>): the text of a text field; the selected option of a list or combo box; the state of a check
    /// box or radio button group (such as <c>Yes</c> or the chosen button's name; <c>Off</c> when none is on);
    /// <c>null</c> when there is none, and for push buttons. For a signature field, <c>Signed</c> when it is signed.
    /// </summary>
    public string? Value => Values.Count > 0 ? Values[0] : null;

    /// <summary>Every value: list boxes that allow several selections can have more than one.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>The options of a list or combo box, as shown (<c>/Opt</c>); empty for other fields.</summary>
    public IReadOnlyList<string> Options { get; }

    /// <summary>Whether the field may not be changed (read-only flag).</summary>
    public bool IsReadOnly { get; }

    /// <summary>Whether the field must have a value when the form is submitted (required flag).</summary>
    public bool IsRequired { get; }

    /// <summary>Where the field is shown: its widget annotations, on the document's current pages.</summary>
    public IReadOnlyList<PdfFormWidget> Widgets { get; }

    /// <summary>The field's name, type and value.</summary>
    public override string ToString() => $"{FullName} ({Type}) = {Value ?? "(none)"}";
}

/// <summary>A widget: where a form field is shown on a page.</summary>
/// <param name="PageIndex">The page's position in the document, from 0.</param>
/// <param name="Rectangle">The widget's rectangle, in the page's user space.</param>
/// <param name="IsHidden">Whether the widget is hidden (its hidden or no-view flag).</param>
/// <param name="State">The appearance state of a check box or radio button widget (<c>/AS</c>, such as <c>Yes</c> or <c>Off</c>), or <c>null</c>.</param>
public sealed record PdfFormWidget(int PageIndex, PdfRectangle Rectangle, bool IsHidden, string? State);
