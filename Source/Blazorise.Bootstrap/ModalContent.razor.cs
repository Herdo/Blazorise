﻿#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
#endregion

namespace Blazorise.Bootstrap.BootstrapBase
{
    public abstract class BaseBootstrapModalContent : BaseModalContent
    {
        #region Members

        #endregion

        #region Constructors

        public BaseBootstrapModalContent()
        {
            DialogClassBuilder = new ClassBuilder( BuildDialogClasses );
        }

        #endregion

        #region Methods

        protected override void DirtyClasses()
        {
            DialogClassBuilder.Dirty();

            base.DirtyClasses();
        }

        private void BuildDialogClasses( ClassBuilder builder )
        {
            builder.Append( $"modal-dialog {ClassProvider.ToModalSize( Size )}" );
            builder.Append( ClassProvider.ModalContentCentered(), IsCentered );
        }

        #endregion

        #region Properties

        protected ClassBuilder DialogClassBuilder { get; private set; }

        /// <summary>
        /// Gets dialog container class-names.
        /// </summary>
        protected string DialogClassNames => DialogClassBuilder.Class;

        #endregion
    }
}
