﻿#region Using directives

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Blazorise.DataGrid.Utils;
using Microsoft.AspNetCore.Components;

#endregion

namespace Blazorise.DataGrid
{
    public abstract class BaseDataGrid<TItem> : BaseComponent
    {
        #region Members

        /// <summary>
        /// Original data-source.
        /// </summary>
        private IEnumerable<TItem> data;

        /// <summary>
        /// Holds the filtered data based on the filter.
        /// </summary>
        private List<TItem> filteredData = new List<TItem>();

        /// <summary>
        /// Holds the filtered data to display based on the current page.
        /// </summary>
        private IEnumerable<TItem> viewData;

        /// <summary>
        /// Marks the grid to reload entire data source based on the current filter settings.
        /// </summary>
        private bool dirtyFilter = true;

        /// <summary>
        /// Marks the grid to refresh currently visible page.
        /// </summary>
        private bool dirtyView = true;

        // Sorting by multiple columns is not ready yet because of the bug in Mono runtime.
        // issue https://github.com/aspnet/AspNetCore/issues/11371
        // Until the bug is fixed only one column will be supported.
        protected BaseDataGridColumn<TItem> sortByColumn = null;

        private readonly Lazy<Func<TItem>> newItemCreator;

        /// <summary>
        /// Currently editing item.
        /// </summary>
        protected TItem editItem;

        /// <summary>
        /// State of the currently editing item.
        /// </summary>
        protected DataGridEditState editState = DataGridEditState.None;

        protected Dictionary<string, CellEditContext> editItemCellValues;

        protected Dictionary<string, CellEditContext> filterCellValues;

        #endregion

        #region Constructors

        public BaseDataGrid()
        {
            newItemCreator = new Lazy<Func<TItem>>( () => FunctionCompiler.CreateNewItem<TItem>() );
        }

        #endregion

        #region Methods

        #region Initialisation

        /// <summary>
        /// Links the child column with this datagrid.
        /// </summary>
        /// <param name="column">Column to link with this datagrid.</param>
        internal void Hook( BaseDataGridColumn<TItem> column )
        {
            Columns.Add( column );

            // save command column reference for later
            if ( CommandColumn == null && column is DataGridCommandColumn<TItem> commandColumn )
            {
                CommandColumn = commandColumn;
            }
        }

        protected override Task OnFirstAfterRenderAsync()
        {
            if ( ReadData.HasDelegate )
                return HandleReadData();

            // after all the columns have being "hooked" we need to resfresh the grid
            StateHasChanged();

            return base.OnFirstAfterRenderAsync();
        }

        #endregion

        #region Editing

        /// <summary>
        /// Create new empty instance of TItem.
        /// </summary>
        /// <returns>Return new instance of TItem.</returns>
        private TItem CreateNewItem()
            => newItemCreator.Value();

        /// <summary>
        /// Prepares edit item and it's cell values for editing.
        /// </summary>
        /// <param name="item">Item to set.</param>
        private void InitEditItem( TItem item )
        {
            editItem = item;
            editItemCellValues = new Dictionary<string, CellEditContext>();

            foreach ( var column in EditableColumns )
            {
                editItemCellValues.Add( column.ElementId, new CellEditContext
                {
                    CellValue = column.GetValue( editItem ),
                } );
            }
        }

        protected Task OnSelectedCommand( TItem item )
        {
            return SelectRow( item );
        }

        protected void OnNewCommand()
        {
            InitEditItem( CreateNewItem() );

            editState = DataGridEditState.New;

            if ( EditMode == DataGridEditMode.Popup )
                PopupVisible = true;
        }

        protected void OnEditCommand( TItem item )
        {
            InitEditItem( item );

            editState = DataGridEditState.Edit;

            if ( EditMode == DataGridEditMode.Popup )
                PopupVisible = true;
        }

        protected async Task OnDeleteCommand( TItem item )
        {
            if ( Data is ICollection<TItem> data )
            {
                if ( IsSafeToProceed( RowRemoving, item ) )
                {
                    if ( UseInternalEditing )
                    {
                        if ( data.Contains( item ) )
                            data.Remove( item );
                    }

                    await RowRemoved.InvokeAsync( item );

                    dirtyFilter = dirtyView = true;
                }
            }
        }

        protected async Task OnSaveCommand()
        {
            if ( Data is ICollection<TItem> data )
            {
                // get the list of edited values
                var editedCellValues = EditableColumns
                    .Select( c => new { c.Field, editItemCellValues[c.ElementId].CellValue } ).ToDictionary( x => x.Field, x => x.CellValue );

                var rowSavingHandler = editState == DataGridEditState.New ? RowInserting : RowUpdating;

                if ( IsSafeToProceed( rowSavingHandler, editItem, editedCellValues ) )
                {
                    if ( UseInternalEditing && editState == DataGridEditState.New && CanInsertNewItem )
                    {
                        data.Add( editItem );
                    }

                    if ( UseInternalEditing || editState == DataGridEditState.New )
                    {
                        // apply edited cell values to the item
                        // for new items it must be always be set, while for editing items it can be set only if it's enabled
                        foreach ( var column in EditableColumns )
                        {
                            column.SetValue( editItem, editItemCellValues[column.ElementId].CellValue );
                        }
                    }

                    if ( editState == DataGridEditState.New )
                    {
                        await RowInserted.InvokeAsync( new SavedRowItem<TItem, Dictionary<string, object>>( editItem, editedCellValues ) );
                        dirtyFilter = dirtyView = true;
                    }
                    else
                        await RowUpdated.InvokeAsync( new SavedRowItem<TItem, Dictionary<string, object>>( editItem, editedCellValues ) );

                    editState = DataGridEditState.None;

                    if ( EditMode == DataGridEditMode.Popup )
                        PopupVisible = false;
                }
            }
        }

        protected void OnCancelCommand()
        {
            editState = DataGridEditState.None;

            if ( EditMode == DataGridEditMode.Popup )
                PopupVisible = false;
        }

        // this is to give user a way to stop save if necessary
        internal bool IsSafeToProceed<TValues>( Action<CancellableRowChange<TItem, TValues>> handler, TItem item, TValues editedCellValues )
        {
            if ( handler != null )
            {
                var args = new CancellableRowChange<TItem, TValues>( item, editedCellValues );

                foreach ( Action<CancellableRowChange<TItem, TValues>> subHandler in handler?.GetInvocationList() )
                {
                    subHandler( args );

                    if ( args.Cancel )
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        internal bool IsSafeToProceed( Action<CancellableRowChange<TItem>> handler, TItem item )
        {
            if ( handler != null )
            {
                var args = new CancellableRowChange<TItem>( item );

                foreach ( Action<CancellableRowChange<TItem>> subHandler in handler?.GetInvocationList() )
                {
                    subHandler( args );

                    if ( args.Cancel )
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        #endregion

        #region Filtering

        protected Task HandleReadData()
        {
            return ReadData.InvokeAsync( new DataGridReadDataEventArgs<TItem>( CurrentPage, PageSize, Columns ) );
        }

        protected Task OnSortClicked( BaseDataGridColumn<TItem> column )
        {
            if ( Sortable && column.Sortable )
            {
                column.Direction = column.Direction == SortDirection.Descending ? SortDirection.Ascending : SortDirection.Descending;
                sortByColumn = column;

                // just one column can be sorted for now!
                foreach ( var col in Columns )
                {
                    if ( col.ElementId == column.ElementId )
                        continue;

                    // reset all others
                    col.Direction = SortDirection.Ascending;
                }

                dirtyFilter = dirtyView = true;

                if ( ReadData.HasDelegate )
                    return HandleReadData();
            }

            return Task.CompletedTask;
        }

        protected Task OnFilterChanged( BaseDataGridColumn<TItem> column, string value )
        {
            column.Filter.SearchValue = value;
            dirtyFilter = dirtyView = true;

            if ( ReadData.HasDelegate )
                return HandleReadData();

            return Task.CompletedTask;
        }

        protected Task OnClearFilterCommand()
        {
            foreach ( var column in Columns )
            {
                column.Filter.SearchValue = null;
            }

            dirtyFilter = dirtyView = true;

            if ( ReadData.HasDelegate )
                return HandleReadData();

            return Task.CompletedTask;
        }

        protected async Task OnPaginationItemClick( string pageName )
        {
            if ( int.TryParse( pageName, out var pageNumber ) )
                CurrentPage = pageNumber;
            else
            {
                if ( pageName == "prev" )
                {
                    CurrentPage--;

                    if ( CurrentPage < 1 )
                        CurrentPage = 1;
                }
                else if ( pageName == "next" )
                {
                    CurrentPage++;

                    if ( CurrentPage > LastPage )
                        CurrentPage = LastPage;
                }
            }

            await PageChanged.InvokeAsync( new DataGridPageChangedEventArgs( CurrentPage, PageSize ) );

            if ( ReadData.HasDelegate )
                await HandleReadData();
        }

        private void FilterData()
        {
            FilterData( Data?.AsQueryable() );
        }

        private void FilterData( IQueryable<TItem> query )
        {
            if ( query == null )
            {
                filteredData.Clear();
                return;
            }

            // only use internal filtering if we're not using custom data loading
            if ( !ReadData.HasDelegate )
            {
                // just one column can be sorted for now!
                if ( sortByColumn != null && sortByColumn.Sortable )
                {
                    if ( sortByColumn.Direction == SortDirection.Descending )
                        query = query.OrderByDescending( item => sortByColumn.GetValue( item ) );
                    else
                        query = query.OrderBy( item => sortByColumn.GetValue( item ) );
                }

                foreach ( var column in Columns )
                {
                    if ( column.ColumnType == DataGridColumnType.Command )
                        continue;

                    if ( string.IsNullOrEmpty( column.Filter.SearchValue ) )
                        continue;

                    query = from q in query
                            let cellRealValue = column.GetValue( q )
                            let cellStringValue = cellRealValue == null ? string.Empty : cellRealValue.ToString()
                            where CompareFilterValues( cellStringValue, column.Filter.SearchValue )
                            select q;
                }
            }

            filteredData = query.ToList();

            dirtyFilter = false;
        }

        private bool CompareFilterValues( string searchValue, string compareTo )
        {
            switch ( FilterMethod )
            {
                case DataGridFilterMethod.StartsWith:
                    return searchValue.StartsWith( compareTo, StringComparison.OrdinalIgnoreCase );
                case DataGridFilterMethod.EndsWith:
                    return searchValue.EndsWith( compareTo, StringComparison.OrdinalIgnoreCase );
                case DataGridFilterMethod.Equals:
                    return searchValue.Equals( compareTo, StringComparison.OrdinalIgnoreCase );
                case DataGridFilterMethod.NotEquals:
                    return !searchValue.Equals( compareTo, StringComparison.OrdinalIgnoreCase );
                case DataGridFilterMethod.Contains:
                default:
                    return searchValue.IndexOf( compareTo, StringComparison.OrdinalIgnoreCase ) >= 0;
            }
        }

        private IEnumerable<TItem> FilterViewData()
        {
            if ( dirtyFilter )
                FilterData();

            // only use pagination if the custom data loading is not used
            if ( !ReadData.HasDelegate )
                return filteredData.Skip( ( CurrentPage - 1 ) * PageSize ).Take( PageSize );

            return filteredData;
        }

        public Task SelectRow( TItem item )
        {
            if ( editState != DataGridEditState.None )
                return Task.CompletedTask;

            SelectedRow = item;
            return SelectedRowChanged.InvokeAsync( SelectedRow );
        }

        #endregion

        #endregion

        #region Properties

        /// <summary>
        /// List of all the columns associated with this datagrid.
        /// </summary>
        protected List<BaseDataGridColumn<TItem>> Columns { get; } = new List<BaseDataGridColumn<TItem>>();

        /// <summary>
        /// Gets only columns that are available for editing.
        /// </summary>
        protected IEnumerable<BaseDataGridColumn<TItem>> EditableColumns => Columns.Where( x => x.ColumnType != DataGridColumnType.Command && x.Editable );

        /// <summary>
        /// Returns true if <see cref="Data"/> is safe to modify.
        /// </summary>
        protected bool CanInsertNewItem => Editable && Data is ICollection<TItem>;

        /// <summary>
        /// Gets the current datagrid editing state.
        /// </summary>
        public DataGridEditState EditState => editState;

        /// <summary>
        /// Gets the flag which indicates if popup editor is visible.
        /// </summary>
        protected bool PopupVisible = false;

        /// <summary>
        /// Gets the reference to the associated command column.
        /// </summary>
        public DataGridCommandColumn<TItem> CommandColumn { get; private set; }

        /// <summary>
        /// Gets or sets the datagrid data-source.
        /// </summary>
        [Parameter]
        public IEnumerable<TItem> Data
        {
            get { return data; }
            set
            {
                data = value;

                // make sure everything is recalculated
                dirtyFilter = dirtyView = true;
            }
        }

        /// <summary>
        /// Gets or sets the total number of items. Used only when <see cref="ReadData"/> is used to load the data.
        /// </summary>
        /// <remarks>
        /// This field must be set only when <see cref="ReadData"/> is used to load the data.
        /// </remarks>
        [Parameter] public int? TotalItems { get; set; }

        /// <summary>
        /// Gets the data after all of the filters have being applied.
        /// </summary>
        protected IEnumerable<TItem> FilteredData
        {
            get
            {
                if ( dirtyFilter )
                    FilterData();

                return filteredData;
            }
        }

        /// <summary>
        /// Gets the data to show on grid based on the filter and current page.
        /// </summary>
        protected IEnumerable<TItem> DisplayData
        {
            get
            {
                if ( dirtyView )
                    viewData = FilterViewData();

                return viewData;
            }
        }

        /// <summary>
        /// Specifies the behaviour of datagrid editing.
        /// </summary>
        /// <remarks>
        /// Disabling this option will send all changes to the RowInserted and RowUpdated but nothing will be saved unless the user manually update the item values.
        /// </remarks>
        [Parameter] public bool UseInternalEditing { get; set; } = true;

        /// <summary>
        /// Gets or sets whether users can edit datagrid rows.
        /// </summary>
        [Parameter] public bool Editable { get; set; }

        /// <summary>
        /// Gets or sets whether end-users can sort data by the column's values.
        /// </summary>
        [Parameter] public bool Sortable { get; set; } = true;

        /// <summary>
        /// Gets or sets whether users can filter rows by its cell values.
        /// </summary>
        [Parameter] public bool Filterable { get; set; }

        /// <summary>
        /// Gets or sets whether user can see a column captions.
        /// </summary>
        [Parameter] public bool ShowCaptions { get; set; } = true;

        /// <summary>
        /// Gets or sets whether users can navigate datagrid by using pagination controls.
        /// </summary>
        [Parameter] public bool ShowPager { get; set; }

        /// <summary>
        /// Gets or sets the current page number.
        /// </summary>
        [Parameter] public int CurrentPage { get; set; } = 1;

        /// <summary>
        /// Gets the last page number.
        /// </summary>
        protected int LastPage
        {
            get
            {
                // if we're using ReadData than TotalItems must be set so we can know how many items are available
                var totalItems = ( ReadData.HasDelegate ? TotalItems : FilteredData?.Count() ) ?? 0;

                var lastPage = Math.Max( (int)Math.Ceiling( totalItems / (double)PageSize ), 1 );

                if ( CurrentPage > lastPage )
                    CurrentPage = lastPage;

                return lastPage;
            }
        }

        /// <summary>
        /// Gets the number of the first page that can be clicked in a large dataset.
        /// </summary>
        protected int FirstVisiblePage
        {
            get
            {
                int firstVisiblePage = CurrentPage - (int)Math.Floor( MaxPaginationLinks / 2d );

                if ( firstVisiblePage < 1 )
                    firstVisiblePage = 1;

                return firstVisiblePage;
            }
        }

        /// <summary>
        /// Gets the number of the last page that can be clicked in a large dataset.
        /// </summary>
        protected int LastVisiblePage
        {
            get
            {
                var firstVisiblePage = FirstVisiblePage;
                var lastVisiblePage = CurrentPage + (int)Math.Floor( MaxPaginationLinks / 2d );

                if ( ( lastVisiblePage - firstVisiblePage ) < MaxPaginationLinks )
                    lastVisiblePage = firstVisiblePage + MaxPaginationLinks - 1;

                if ( lastVisiblePage > LastPage )
                    lastVisiblePage = LastPage;

                return lastVisiblePage;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of items for each page.
        /// </summary>
        [Parameter] public int PageSize { get; set; } = 5;

        /// <summary>
        /// Gets or sets the maximum number of visible pagination links.
        /// </summary>
        [Parameter] public int MaxPaginationLinks { get; set; } = 5;

        /// <summary>
        /// Defines the filter method when searching the cell values.
        /// </summary>
        [Parameter] public DataGridFilterMethod FilterMethod { get; set; } = DataGridFilterMethod.Contains;

        /// <summary>
        /// Gets or sets currently selected row.
        /// </summary>
        [Parameter] public TItem SelectedRow { get; set; }

        /// <summary>
        /// Occurs after the selected row has changed.
        /// </summary>
        [Parameter] public EventCallback<TItem> SelectedRowChanged { get; set; }

        /// <summary>
        /// Cancelable event called before the row is inserted.
        /// </summary>
        [Parameter] public Action<CancellableRowChange<TItem, Dictionary<string, object>>> RowInserting { get; set; }

        /// <summary>
        /// Cancelable event called before the row is updated.
        /// </summary>
        [Parameter] public Action<CancellableRowChange<TItem, Dictionary<string, object>>> RowUpdating { get; set; }

        /// <summary>
        /// Cancelable event called before the row is removed.
        /// </summary>
        [Parameter] public Action<CancellableRowChange<TItem>> RowRemoving { get; set; }

        /// <summary>
        /// Event called after the row is inserted.
        /// </summary>
        [Parameter] public EventCallback<SavedRowItem<TItem, Dictionary<string, object>>> RowInserted { get; set; }

        /// <summary>
        /// Event called after the row is updated.
        /// </summary>
        [Parameter] public EventCallback<SavedRowItem<TItem, Dictionary<string, object>>> RowUpdated { get; set; }

        /// <summary>
        /// Event called after the row is removed.
        /// </summary>
        [Parameter] public EventCallback<TItem> RowRemoved { get; set; }

        /// <summary>
        /// Occurs after the selected page has changed.
        /// </summary>
        [Parameter] public EventCallback<DataGridPageChangedEventArgs> PageChanged { get; set; }

        /// <summary>
        /// Event handler used to load data manually based on the current page and filter data settings.
        /// </summary>
        [Parameter] public EventCallback<DataGridReadDataEventArgs<TItem>> ReadData { get; set; }

        /// <summary>
        /// Specifes the grid editing modes.
        /// </summary>
        [Parameter] public DataGridEditMode EditMode { get; set; } = DataGridEditMode.Form;

        /// <summary>
        /// A trigger function used to handle the visibility of detail row.
        /// </summary>
        [Parameter] public Func<TItem, bool> DetailRowTrigger { get; set; }

        /// <summary>
        /// Handles the selection of the clicked row.
        /// If not set it will default to always true.
        /// </summary>
        [Parameter] public Func<TItem, bool> RowSelectable { get; set; }

        /// <summary>
        /// Handles the selection of the cursor for a hovered row.
        /// If not set, <see cref="Cursor.Default"/> will be used.
        /// </summary>
        [Parameter] public Func<TItem, Cursor> RowHoverCursor { get; set; }

        /// <summary>
        /// Template for displaying detail or nested row.
        /// </summary>
        [Parameter] public RenderFragment<TItem> DetailRowTemplate { get; set; }

        /// <summary>
        /// Adds stripes to the table.
        /// </summary>
        [Parameter] public bool IsStriped { get; set; }

        /// <summary>
        /// Adds borders to all the cells.
        /// </summary>
        [Parameter] public bool IsBordered { get; set; }

        /// <summary>
        /// Makes the table without any borders.
        /// </summary>
        [Parameter] public bool IsBorderless { get; set; }

        /// <summary>
        /// Adds a hover effect when mousing over rows.
        /// </summary>
        [Parameter] public bool IsHoverable { get; set; }

        /// <summary>
        /// Makes the table more compact by cutting cell padding in half.
        /// </summary>
        [Parameter] public bool IsNarrow { get; set; }

        [Parameter] public RenderFragment ChildContent { get; set; }

        #endregion
    }
}