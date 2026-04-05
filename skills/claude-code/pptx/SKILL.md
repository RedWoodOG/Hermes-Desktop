---
name: pptx
description: Create and edit PowerPoint presentations with slides, text, images, charts, and formatting.
tools: bash, read_file, write_file, terminal
---

# PowerPoint (.pptx) Presentations

You are an expert at creating professional PowerPoint presentations using Python. You produce clean, well-designed slides.

## Setup

```bash
pip install python-pptx 2>/dev/null
```

## Operations

### Create a Presentation

```python
from pptx import Presentation
from pptx.util import Inches, Pt, Emu
from pptx.dml.color import RGBColor
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR

prs = Presentation()
prs.slide_width = Inches(13.333)
prs.slide_height = Inches(7.5)

# Title slide
slide_layout = prs.slide_layouts[0]  # Title Slide layout
slide = prs.slides.add_slide(slide_layout)
title = slide.shapes.title
subtitle = slide.placeholders[1]
title.text = "Presentation Title"
subtitle.text = "Subtitle or Author Name\nDate"

# Content slide with bullets
slide_layout = prs.slide_layouts[1]  # Title and Content layout
slide = prs.slides.add_slide(slide_layout)
title = slide.shapes.title
title.text = "Key Points"
body = slide.placeholders[1]
tf = body.text_frame
tf.text = "First point"
p = tf.add_paragraph()
p.text = "Second point"
p.level = 0
p = tf.add_paragraph()
p.text = "Sub-point under second"
p.level = 1
p = tf.add_paragraph()
p.text = "Third point"
p.level = 0

prs.save("presentation.pptx")
```

### Custom Slides with Shapes

```python
from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor
from pptx.enum.text import PP_ALIGN

prs = Presentation()
blank = prs.slide_layouts[6]  # Blank layout
slide = prs.slides.add_slide(blank)

# Add a text box
txBox = slide.shapes.add_textbox(Inches(1), Inches(1), Inches(8), Inches(1))
tf = txBox.text_frame
p = tf.paragraphs[0]
p.text = "Custom Heading"
p.font.size = Pt(36)
p.font.bold = True
p.font.color.rgb = RGBColor(0x1F, 0x49, 0x7D)
p.alignment = PP_ALIGN.LEFT

# Add a rectangle
shape = slide.shapes.add_shape(
    1,  # MSO_SHAPE.RECTANGLE
    Inches(1), Inches(2.5), Inches(4), Inches(3)
)
shape.fill.solid()
shape.fill.fore_color.rgb = RGBColor(0xE8, 0xF0, 0xFE)
shape.line.color.rgb = RGBColor(0x1F, 0x49, 0x7D)

# Add text to shape
tf = shape.text_frame
tf.word_wrap = True
p = tf.paragraphs[0]
p.text = "Content inside a box"
p.font.size = Pt(14)

prs.save("custom_slides.pptx")
```

### Add Images

```python
from pptx import Presentation
from pptx.util import Inches

prs = Presentation()
slide = prs.slides.add_slide(prs.slide_layouts[6])

# Add image with position and size
slide.shapes.add_picture("image.png", Inches(1), Inches(1), width=Inches(5))

prs.save("with_images.pptx")
```

### Add Tables

```python
from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor

prs = Presentation()
slide = prs.slides.add_slide(prs.slide_layouts[6])

rows, cols = 4, 3
table = slide.shapes.add_table(rows, cols, Inches(1), Inches(1.5), Inches(8), Inches(3)).table

# Set column widths
table.columns[0].width = Inches(3)
table.columns[1].width = Inches(2.5)
table.columns[2].width = Inches(2.5)

# Header row
headers = ['Feature', 'Status', 'Owner']
for i, h in enumerate(headers):
    cell = table.cell(0, i)
    cell.text = h
    for para in cell.text_frame.paragraphs:
        para.font.bold = True
        para.font.size = Pt(14)

# Data rows
data = [
    ['Authentication', 'Complete', 'Alice'],
    ['Dashboard', 'In Progress', 'Bob'],
    ['Reports', 'Planned', 'Charlie'],
]
for r, row_data in enumerate(data, 1):
    for c, val in enumerate(row_data):
        table.cell(r, c).text = val

prs.save("with_table.pptx")
```

### Add Charts

```python
from pptx import Presentation
from pptx.chart.data import CategoryChartData
from pptx.enum.chart import XL_CHART_TYPE
from pptx.util import Inches

prs = Presentation()
slide = prs.slides.add_slide(prs.slide_layouts[6])

chart_data = CategoryChartData()
chart_data.categories = ['Q1', 'Q2', 'Q3', 'Q4']
chart_data.add_series('Revenue', (120, 180, 210, 250))
chart_data.add_series('Costs', (80, 90, 105, 110))

chart = slide.shapes.add_chart(
    XL_CHART_TYPE.COLUMN_CLUSTERED,
    Inches(1), Inches(1.5), Inches(8), Inches(5),
    chart_data
).chart

chart.has_legend = True
chart.legend.include_in_layout = False

prs.save("with_chart.pptx")
```

## Workflow

1. **Plan the deck** - How many slides? What content per slide? What visual style?
2. **Create slide structure** - Title slide, content slides, closing slide
3. **Add content** - Text, images, tables, charts
4. **Apply consistent formatting** - Colors, fonts, alignment
5. **Verify** - Check slide count and content
6. **Report** - Provide the file path

## Design Principles

- Keep text minimal - use bullet points, not paragraphs
- One main idea per slide
- Use consistent fonts and colors throughout
- Leave whitespace - don't crowd slides
- Use images and charts to convey data visually
- Limit bullet points to 5-7 per slide
- Use slide layouts that match the content type
