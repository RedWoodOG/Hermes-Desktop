---
name: pdf
description: PDF manipulation - read, merge, split, create PDFs, and extract text or tables.
tools: bash, read_file, write_file, terminal
---

# PDF Manipulation

You are an expert at working with PDF files. You can read, create, merge, split, and extract content from PDFs using Python libraries.

## Setup

First, ensure required libraries are available:

```bash
pip install PyPDF2 pdfplumber reportlab tabulate 2>/dev/null
```

If `pip` is not available, try `pip3`. If running in a restricted environment, check what's already installed:

```bash
python -c "import PyPDF2; print('PyPDF2 available')" 2>/dev/null
python -c "import pdfplumber; print('pdfplumber available')" 2>/dev/null
python -c "import reportlab; print('reportlab available')" 2>/dev/null
```

## Operations

### Read / Extract Text from PDF

```python
import pdfplumber

with pdfplumber.open("input.pdf") as pdf:
    for i, page in enumerate(pdf.pages):
        text = page.extract_text()
        print(f"--- Page {i+1} ---")
        print(text)
```

For scanned PDFs (images), you'll need OCR:
```bash
pip install pytesseract pdf2image
# Also requires tesseract system package
```

### Extract Tables from PDF

```python
import pdfplumber

with pdfplumber.open("input.pdf") as pdf:
    for page in pdf.pages:
        tables = page.extract_tables()
        for table in tables:
            for row in table:
                print(row)
```

### Create a PDF from Text/Markdown

```python
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer
from reportlab.lib.units import inch

doc = SimpleDocTemplate("output.pdf", pagesize=letter)
styles = getSampleStyleSheet()
story = []

story.append(Paragraph("Document Title", styles['Title']))
story.append(Spacer(1, 0.3 * inch))
story.append(Paragraph("Body text goes here. ReportLab supports basic HTML-like formatting.", styles['Normal']))

doc.build(story)
```

### Merge Multiple PDFs

```python
from PyPDF2 import PdfMerger

merger = PdfMerger()
merger.append("file1.pdf")
merger.append("file2.pdf")
merger.append("file3.pdf")
merger.write("merged.pdf")
merger.close()
```

### Split a PDF

```python
from PyPDF2 import PdfReader, PdfWriter

reader = PdfReader("input.pdf")

# Extract specific pages (0-indexed)
writer = PdfWriter()
writer.add_page(reader.pages[0])  # First page
writer.add_page(reader.pages[2])  # Third page

with open("extracted.pdf", "wb") as f:
    writer.write(f)
```

### Split into Individual Pages

```python
from PyPDF2 import PdfReader, PdfWriter

reader = PdfReader("input.pdf")
for i, page in enumerate(reader.pages):
    writer = PdfWriter()
    writer.add_page(page)
    with open(f"page_{i+1}.pdf", "wb") as f:
        writer.write(f)
```

### Rotate Pages

```python
from PyPDF2 import PdfReader, PdfWriter

reader = PdfReader("input.pdf")
writer = PdfWriter()

for page in reader.pages:
    page.rotate(90)  # 90, 180, or 270 degrees
    writer.add_page(page)

with open("rotated.pdf", "wb") as f:
    writer.write(f)
```

### Get PDF Metadata

```python
from PyPDF2 import PdfReader

reader = PdfReader("input.pdf")
print(f"Pages: {len(reader.pages)}")
meta = reader.metadata
if meta:
    print(f"Title: {meta.title}")
    print(f"Author: {meta.author}")
    print(f"Created: {meta.creation_date}")
```

## Workflow

1. **Identify the task** - What does the user want? Read, create, merge, split, extract?
2. **Check the input** - Verify files exist, check page counts, read metadata
3. **Perform the operation** - Use the appropriate technique above
4. **Verify the output** - Check the result (page count, file size, sample content)
5. **Report** - Tell the user what was created and where

## Tips

- Always verify input files exist before processing
- For large PDFs, process pages in batches to manage memory
- When extracting text, `pdfplumber` generally works better than `PyPDF2` for text extraction
- For creating styled documents, `reportlab` is the most capable library
- Always provide the output file path to the user
