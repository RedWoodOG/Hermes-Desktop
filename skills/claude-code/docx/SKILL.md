---
name: docx
description: Create, read, edit, and manipulate Word documents (.docx files).
tools: bash, read_file, write_file, terminal
---

# Word Document (.docx) Manipulation

You are an expert at creating and editing Microsoft Word documents using Python. You produce professional, well-formatted documents.

## Setup

```bash
pip install python-docx 2>/dev/null
```

## Operations

### Create a New Document

```python
from docx import Document
from docx.shared import Inches, Pt, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH

doc = Document()

# Title
title = doc.add_heading('Document Title', level=0)

# Subtitle or intro paragraph
p = doc.add_paragraph()
run = p.add_run('Subtitle or introductory text')
run.font.size = Pt(14)
run.font.color.rgb = RGBColor(100, 100, 100)

# Section heading
doc.add_heading('Section 1', level=1)

# Body text
doc.add_paragraph('This is a body paragraph with normal formatting.')

# Bold and italic
p = doc.add_paragraph()
p.add_run('Bold text').bold = True
p.add_run(' and ')
p.add_run('italic text').italic = True

# Bullet list
doc.add_paragraph('First item', style='List Bullet')
doc.add_paragraph('Second item', style='List Bullet')
doc.add_paragraph('Third item', style='List Bullet')

# Numbered list
doc.add_paragraph('Step one', style='List Number')
doc.add_paragraph('Step two', style='List Number')

# Table
table = doc.add_table(rows=3, cols=3, style='Table Grid')
headers = table.rows[0].cells
headers[0].text = 'Column 1'
headers[1].text = 'Column 2'
headers[2].text = 'Column 3'
for i in range(1, 3):
    for j in range(3):
        table.rows[i].cells[j].text = f'Row {i}, Col {j+1}'

# Page break
doc.add_page_break()

# Second page content
doc.add_heading('Section 2', level=1)
doc.add_paragraph('Content on the second page.')

doc.save('output.docx')
```

### Read a Document

```python
from docx import Document

doc = Document('input.docx')

for para in doc.paragraphs:
    print(f"[{para.style.name}] {para.text}")

# Read tables
for table in doc.tables:
    for row in table.rows:
        print([cell.text for cell in row.cells])
```

### Edit an Existing Document

```python
from docx import Document

doc = Document('input.docx')

# Find and replace text
for para in doc.paragraphs:
    if 'OLD_TEXT' in para.text:
        for run in para.runs:
            run.text = run.text.replace('OLD_TEXT', 'NEW_TEXT')

# Add content at the end
doc.add_heading('New Section', level=1)
doc.add_paragraph('Additional content appended to the document.')

doc.save('output.docx')
```

### Add Images

```python
from docx import Document
from docx.shared import Inches

doc = Document()
doc.add_heading('Report with Images', level=0)
doc.add_picture('image.png', width=Inches(5.0))
doc.save('with_image.docx')
```

### Format Headers and Footers

```python
from docx import Document
from docx.shared import Pt

doc = Document()
section = doc.sections[0]

header = section.header
header_para = header.paragraphs[0]
header_para.text = "Company Name - Confidential"

footer = section.footer
footer_para = footer.paragraphs[0]
footer_para.text = "Page footer text"

doc.save('with_headers.docx')
```

### Set Page Margins

```python
from docx import Document
from docx.shared import Inches

doc = Document()
for section in doc.sections:
    section.top_margin = Inches(1)
    section.bottom_margin = Inches(1)
    section.left_margin = Inches(1.25)
    section.right_margin = Inches(1.25)

doc.save('custom_margins.docx')
```

## Workflow

1. **Clarify the request** - What type of document? What content and formatting?
2. **Prepare content** - Gather text, data, images from user or files
3. **Create/edit the document** - Use python-docx to build the document
4. **Verify** - Read back key sections to confirm correctness
5. **Report** - Provide the output file path

## Style Guide

- Use heading levels consistently (0 for title, 1 for sections, 2 for subsections)
- Use built-in styles where possible (`Normal`, `Heading 1`, `List Bullet`, `Table Grid`)
- Set reasonable margins (1 inch is standard)
- Use 11pt or 12pt for body text
- Include page breaks between major sections
- Keep tables simple and readable
