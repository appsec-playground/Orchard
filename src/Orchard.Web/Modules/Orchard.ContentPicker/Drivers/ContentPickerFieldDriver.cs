﻿using System;
using System.Linq;
using Orchard.ContentPicker.Settings;
using Orchard.ContentManagement;
using Orchard.ContentManagement.Drivers;
using Orchard.ContentManagement.Handlers;
using Orchard.ContentPicker.ViewModels;
using Orchard.Localization;
using Orchard.Utility.Extensions;
using Orchard.ContentPicker.Fields;
using Orchard.Tokens;
using System.Collections.Generic;

namespace Orchard.ContentPicker.Drivers {
    public class ContentPickerFieldDriver : ContentFieldDriver<Fields.ContentPickerField> {
        private readonly IContentManager _contentManager;
        private readonly ITokenizer _tokenizer;

        public ContentPickerFieldDriver(
            IContentManager contentManager,
            ITokenizer tokenizer) {
            _contentManager = contentManager;
            _tokenizer = tokenizer;
            T = NullLocalizer.Instance;
        }

        public Localizer T { get; set; }

        private static string GetPrefix(Fields.ContentPickerField field, ContentPart part) {
            return part.PartDefinition.Name + "." + field.Name;
        }

        private static string GetDifferentiator(Fields.ContentPickerField field, ContentPart part) {
            return field.Name;
        }

        protected override DriverResult Display(ContentPart part, Fields.ContentPickerField field, string displayType, dynamic shapeHelper) {
            return Combined(
                ContentShape("Fields_ContentPicker", GetDifferentiator(field, part), () => shapeHelper.Fields_ContentPicker()),
                ContentShape("Fields_ContentPicker_SummaryAdmin", GetDifferentiator(field, part), () => {
                    var unpublishedIds = field.Ids.Except(field.ContentItems.Select(x => x.Id));
                    var unpublishedContentItems = _contentManager.GetMany<ContentItem>(unpublishedIds, VersionOptions.Latest, QueryHints.Empty).ToList();

                    return shapeHelper.Fields_ContentPicker_SummaryAdmin(UnpublishedContentItems: unpublishedContentItems);
                }));
        }

        protected override DriverResult Editor(ContentPart part, Fields.ContentPickerField field, dynamic shapeHelper) {
            return ContentShape("Fields_ContentPicker_Edit", GetDifferentiator(field, part),
                () => {
                    var ids = part.IsNew()
                        ? GetDefaultids(part, field)
                        : field.Ids;
                    var model = new ContentPickerFieldViewModel {
                        Field = field,
                        Part = part,
                        ContentItems = _contentManager
                            .GetMany<ContentItem>(ids, VersionOptions.Latest, QueryHints.Empty).ToList()
                    };

                    model.SelectedIds = string.Join(",", ids);

                    return shapeHelper.EditorTemplate(TemplateName: "Fields/ContentPicker.Edit", Model: model, Prefix: GetPrefix(field, part));
                });
        }

        private int[] GetDefaultids(ContentPart part, Fields.ContentPickerField field) {
            var ids = new int[] { };
            var settings = field.PartFieldDefinition.Settings.GetModel<ContentPickerFieldSettings>();
            if (!string.IsNullOrWhiteSpace(settings?.DefaultValue)) {
                var defaultIds = _tokenizer
                    .Replace(settings.DefaultValue,
                        new Dictionary<string, object> { { "Content", part.ContentItem } });
                if (!string.IsNullOrWhiteSpace(defaultIds)) {
                    // attempt to parse the string we populated from tokens
                    ids = ContentPickerField.DecodeIds(defaultIds);
                }
            }

            return ids;
        }

        protected override DriverResult Editor(ContentPart part, Fields.ContentPickerField field, IUpdateModel updater, dynamic shapeHelper) {
            var model = new ContentPickerFieldViewModel { SelectedIds = string.Join(",", field.Ids) };

            updater.TryUpdateModel(model, GetPrefix(field, part), null, null);

            var settings = field.PartFieldDefinition.Settings.GetModel<ContentPickerFieldSettings>();

            if (String.IsNullOrEmpty(model.SelectedIds)) {
                field.Ids = new int[0];
            } else {
                field.Ids = model.SelectedIds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            }

            if (settings.Required && field.Ids.Length == 0) {
                updater.AddModelError("Id", T("The {0} field is required.", field.Name.CamelFriendly()));
            }

            return Editor(part, field, shapeHelper);
        }

        protected override void Importing(ContentPart part, Fields.ContentPickerField field, ImportContentContext context) {
            // If nothing about the field is inside the context, field is not modified.
            // For this reason, check if the current element is inside the ImportContentContext.
            var element = context.Data.Element(field.FieldDefinition.Name + "." + field.Name);
            if (element != null) {
                var contentItemIds = context.Attribute(field.FieldDefinition.Name + "." + field.Name, "ContentItems");
                if (contentItemIds != null) {
                    field.Ids = contentItemIds.Split(',')
                        .Select(context.GetItemFromSession)
                        .Select(contentItem => contentItem.Id).ToArray();
                } else {
                    field.Ids = new int[0];
                }
            }
        }

        protected override void Exporting(ContentPart part, Fields.ContentPickerField field, ExportContentContext context) {
            if (field.Ids.Any()) {
                var contentItemIds = field.Ids
                    .Select(x => _contentManager.Get(x))
                    .Where(x => x != null)
                    .Select(x => _contentManager.GetItemMetadata(x).Identity.ToString())
                    .ToArray();

                context.Element(field.FieldDefinition.Name + "." + field.Name).SetAttributeValue("ContentItems", string.Join(",", contentItemIds));
            }
        }

        protected override void Cloning(ContentPart part, ContentPickerField originalField, ContentPickerField cloneField, CloneContentContext context) {
            cloneField.Ids = originalField.Ids;
        }

        protected override void Describe(DescribeMembersContext context) {
            context
                .Member(null, typeof(string), T("Ids"), T("A formatted list of the ids, e.g., {1},{42}"));
        }
    }
}