# Flexbox

Flex containers lay out as in browsers: `flex-direction`, `flex-wrap`, `justify-content`, `align-items` and
`align-self` (including `stretch` and `baseline`), `align-content`, `gap`, `order`, `flex`, `flex-grow`,
`flex-shrink`, `flex-basis`, min and max sizes and auto margins. `inline-flex` boxes flow in the text.

[!code-csharp[](../snippets/Flexbox.cs#flex)]

- A container with a height flexes its items vertically too, as in the cover page above: `flex: 1` (or
  `margin-top: auto`) pushes the footer to the bottom of the page. `html, body { height: 100% }` makes the page area
  the height to fill.
- A row too tall for the page is split like browsers split it: each item continues on the next page in its own
  column, and stretched items (a sidebar background, say) run down to the end of the row.
- Right-to-left flex rows start on the right.
- Grid layout is not supported yet: an element with `display: grid` is laid out as a block. A wrapping flex row with
  fixed `flex-basis` values often gives the same result for card layouts.
